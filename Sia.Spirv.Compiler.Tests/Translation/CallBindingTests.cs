using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CallBindingTests
{
    [Fact]
    public void PublicCallRequiresResolvedIdentity()
    {
        var constructor = Assert.Single(typeof(Expression.Call).GetConstructors());
        Assert.Equal(4, constructor.GetParameters().Length);
        Assert.Equal(typeof(CallBinding), constructor.GetParameters()[3].ParameterType);
        Assert.False(constructor.GetParameters()[3].IsOptional);
        Assert.Equal(new[] { "Function", "Builtin" }, Enum.GetNames<CallBinding>());
    }

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(99)]
    public void InvalidBindingDiagnosesAtItsSource(int value)
    {
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}");
        var call = new Expression.Call("abs", [Expression.I32(-3)], ShaderType.I32, (CallBinding)value) { Span = new(18, 5) };
        module.Functions.Single().Body.Statements.Add(new Statement.Evaluate(call));
        var error = Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
        Assert.Contains("Call binding", error.Message); Assert.Equal(call.Span, error.Diagnostic.Span);
        Assert.Throws<ShaderException>(() => CanonicalShaderPipeline.Prepare(module));
        Assert.Equal((CallBinding)value, call.Binding);
    }

    [Fact]
    public void MissingFunctionDoesNotBecomePureBuiltinByName()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}");
        module.Functions.Single().Body.Statements.Add(new Statement.Evaluate(
            new Expression.Call("min", [Expression.U32(3), Expression.U32(7)], ShaderType.U32, CallBinding.Function)));
        var effects = ShaderEffectAnalysis.Compute(module)["main"];
        Assert.True((effects & (ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall))
            == (ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall));
        Assert.Contains("Unknown resolved function", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module)).Message);
    }

    internal static Module SameName(CallBinding binding)
    {
        var module = WgslReader.Parse("@group(0) @binding(1) var<storage,read_write> output:array<u32>; fn min(a:u32,b:u32)->u32{output[1]+=1u;return a+b;} @compute @workgroup_size(1) fn main(){}");
        var global = module.Globals.Single();
        var place = new Expression.Access(new Expression.Reference(global.Name, new ShaderType.Pointer(global.Type, global.Space)),
            Expression.U32(0), new ShaderType.Pointer(ShaderType.U32, global.Space));
        module.Functions.Single(f => f.Stage is not null).Body.Statements.Add(new Statement.Store(place,
            new Expression.Call("min", [Expression.U32(3), Expression.U32(7)], ShaderType.U32, binding)));
        ModuleValidator.Validate(module); return module;
    }

    [Theory] [InlineData(CallBinding.Function)] [InlineData(CallBinding.Builtin)]
    public void SameNameCallsRetainDistinctEffectsGraphIdentityAndExecution(CallBinding binding)
    {
        var module = SameName(binding);
        var expected = binding == CallBinding.Function ? new uint[] { 10, 1 } : new uint[] { 3, 0 };
        Assert.Equal(expected, new CanonicalExecution(module, []).Run().Output);
        var effects = ShaderEffectAnalysis.Compute(module)["main"];
        Assert.Equal(binding == CallBinding.Function, ControlFlowAnalysis.CalledFunctions(module, ["main"]).Contains("min"));
        var canonical = CanonicalShaderPipeline.Prepare(module);
        var calls = canonical.Functions["main"].Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).ToArray();
        if (binding == CallBinding.Function) Assert.Contains(calls, o => o is ValueOperation.Call { Function: "min" });
        else Assert.Contains(calls, o => o is ValueOperation.Builtin { Function: "min" });
        // Canonical function calls retain the declared conservative UnknownCall marker.
        Assert.Equal(effects | (binding == CallBinding.Function ? ShaderEffects.UnknownCall : ShaderEffects.None), ShaderEffectAnalysis.Compute(canonical)["main"]);
        var target = SpirvCompilationTarget.Default;
        foreach (var candidate in new[] { CanonicalShaderPipeline.Run(module), WgslReader.Parse(WgslWriter.Write(module, target)),
            SpirvReader.Parse(SpirvWriter.Write(module, target)) }) {
            ModuleValidator.Validate(candidate);
            Assert.Equal(expected, new CanonicalExecution(candidate, []).Run().Output);
        }
    }
}

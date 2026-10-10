using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalHandleAliasTests
{
    private static bool Handle(ShaderType type) => type is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure or ShaderType.BindingArray;

    [Theory] [InlineData("Image")] [InlineData("Sampler")] [InlineData("Scene")] [InlineData("Descriptor")] [InlineData("Atomic")]
    public void MutableHandlesRemainInvalidAndNeverAllocateOpaqueData(string fixture)
    {
        var module = WgslReader.Parse(HandleAliasFixtures.Source(fixture));
        var function = module.Functions.First(f => f.Body.Statements.OfType<Statement.Declare>().Any(d => Handle(d.Type)));
        int index = function.Body.Statements.FindIndex(s => s is Statement.Declare d && Handle(d.Type));
        function.Body.Statements[index] = ((Statement.Declare)function.Body.Statements[index]) with { Mutable = true };
        Assert.Contains("Mutable local", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module)).Message);
        Assert.False(StructuredControlFlowReader.TryRead(function, module, out _, out var reason));
        Assert.Equal("Declare", reason);
    }

    [Fact]
    public void DescriptorSelectionCapturesItsEffectfulIndexOnce()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(HandleAliasFixtures.Descriptor));
        Assert.Empty(canonical.DeferredFunctions);
        var instructions = canonical.Functions["main"].Blocks.SelectMany(b => b.Instructions).ToArray();
        var choice = Assert.Single(instructions, i => i.Operation is ValueOperation.Call { Function: "choose" });
        var access = Assert.Single(instructions.Select(i => i.Operation).OfType<ValueOperation.Access>(), a => a.Base.Type is ShaderType.BindingArray);
        var capture = Assert.Single(instructions, i => i.Result == access.Index);
        Assert.Equal(choice.Result!.Value, Assert.IsType<ValueOperation.Let>(capture.Operation).Value);
        var target = ShaderTargetLowering.PrepareSpirv(canonical, null);
        Assert.Single(target.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call { Function: "choose" });
        var text = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical));
        Assert.Equal(2, text.Split("choose(", StringSplitOptions.None).Length - 1); // definition and one captured invocation
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void AtomicOriginUsesResolvedSymbolsRatherThanGlobalNames(bool alias)
    {
        var module = WgslReader.Parse(HandleAliasFixtures.Atomic);
        var image = module.Globals.Single();
        var helper = new ShaderFunction("check");
        helper.Arguments.Add(new(alias ? "parameter" : image.Name, image.Type));
        if (alias) helper.Body.Statements.Add(new Statement.Declare(image.Name, image.Type,
            new Expression.Reference("parameter", image.Type), false));
        helper.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("textureAtomicAdd", [
            new Expression.Reference(image.Name, image.Type),
            new Expression.Construct(new ShaderType.Vector(2, ShaderType.I32), [new Expression.Literal(0, ShaderType.I32)]),
            new Expression.Literal(1u, ShaderType.U32)], new ShaderType.Void(), CallBinding.Builtin)));
        module.Functions.Add(helper);
        Assert.Contains("global texture", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module)).Message);
    }

    [Theory] [InlineData("Image")] [InlineData("Sampler")] [InlineData("Scene")] [InlineData("Descriptor")] [InlineData("Atomic")]
    public void HandleAliasesOwnTheirGraphsAndSurviveBothTargets(string fixture)
    {
        var module = WgslReader.Parse(HandleAliasFixtures.Source(fixture));
        var canonical = CanonicalShaderPipeline.Prepare(module);
        Assert.Empty(canonical.DeferredFunctions);
        Assert.Contains(canonical.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Let let && let.Value.Type is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure or ShaderType.BindingArray);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        foreach (var function in module.Functions) {
            function.Body = new(); function.Body.Statements.Add(new Statement.Evaluate(new Expression.Reference("obsolete", ShaderType.U32)));
        }
        ModuleValidator.Validate(canonical);
        var target = ShaderTargetLowering.PrepareSpirv(canonical, null);
        Assert.Empty(target.DeferredFunctions);
        var native = SpirvWriter.Emit(SpirvEntryPointLowering.Run(target, true, true)).ToBytes();
        var imported = SpirvReader.Parse(native); ModuleValidator.Validate(imported);
        var nativeCanonical = CanonicalShaderPipeline.Prepare(imported); Assert.Empty(nativeCanonical.DeferredFunctions);
        var text = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical));
        ModuleValidator.Validate(WgslReader.Parse(text)); Assert.DoesNotContain("obsolete", text);
        _ = WgslWriter.Write(imported, SpirvCompilationTarget.Default);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }
}

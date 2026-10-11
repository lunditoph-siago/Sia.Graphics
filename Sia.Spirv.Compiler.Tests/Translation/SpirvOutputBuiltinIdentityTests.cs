using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvOutputBuiltinIdentityTests
{
    internal const string Ordered = """
        @group(0) @binding(0) var<storage,read> inputs:array<u32>;
        @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
        fn clamp(value:f32,lower:f32,upper:f32)->f32{outputs[1]+=1u;return value;}
        @fragment fn main()->@builtin(frag_depth) f32{return clamp(bitcast<f32>(inputs[0]),0.0,1.0);}
        """;

    internal static Module OrderedProbe()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Ordered), null, true, true, true);
        var helper = Assert.Single(prepared.OutputFunctions).Value;
        string source = Ordered[..Ordered.IndexOf("@fragment", StringComparison.Ordinal)]
            + "fn " + helper.Name + "(value:f32)->f32{return value;}"
            + "@compute @workgroup_size(1) fn main(){outputs[0]=bitcast<u32>(" + helper.Name + "(clamp(bitcast<f32>(inputs[0]),0.0,1.0)));}";
        var module = WgslReader.Parse(source);
        var adapted = StructuredControlFlowLowering.Run(prepared.PhysicalLayout.ControlFlow[helper.Name].Graph, prepared.Module);
        module.Functions.RemoveAll(f => f.Name == helper.Name); module.Functions.Add(adapted);
        ModuleValidator.Validate(module); return module;
    }
    [Fact]
    public void OutputDepthClampRetainsBuiltinIdentityWhenTheSourceShadowsItsName()
    {
        var input = WgslReader.Parse("fn clamp(value:f32,lower:f32,upper:f32)->f32{return value;}"
            + "@fragment fn main()->@builtin(frag_depth) f32{return clamp(2.0,0.0,1.0);}");
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var binary = SpirvWriter.Emit(prepared);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ExtInst && i.Operands.Length >= 4 && i.Operands[3] == 43);
    }

    [Fact]
    public void CanonicalBuiltinIdentitySurvivesReconstructionAndSharedAnalysis()
    {
        var module = OrderedProbe(); var helper = module.Functions.Single(f => f.Name == "sia_spv_output_depth");
        Assert.True(StructuredControlFlowReader.TryRead(helper, module, out var graph, out _));
        ControlFlowVerifier.Validate(graph!, module);
        Assert.Single(graph!.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Builtin { Function: "clamp" });
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call);
        var lowered = CanonicalShaderPipeline.Run(module);
        var rebuilt = lowered.Functions.Single(f => f.Name == helper.Name);
        Assert.Empty(ControlFlowAnalysis.Calls(rebuilt.Body));
        Assert.Equal(ShaderEffects.None, ShaderEffectAnalysis.Compute(lowered)[helper.Name]);
        Assert.True((ShaderEffectAnalysis.Compute(lowered)["clamp"] & ShaderEffects.WriteMemory) != 0);
        Assert.Contains(SpirvBinary.Parse(SpirvWriter.Write(lowered, SpirvCompilationTarget.Default)).Instructions, i => (Op)i.Opcode == Op.ExtInst && i.Operands[3] == 43);
    }

    [Fact]
    public void UserFunctionWithConstantArgumentsIsNotFoldedAsABuiltin()
    {
        var module = WgslReader.Parse("fn clamp(x:f32,a:f32,b:f32)->f32{return x;}@compute @workgroup_size(1) fn main(){let value=clamp(2.0,0.0,1.0);}");
        var call = module.Functions.Single(f => f.Stage is not null).Body.Statements.OfType<Statement.Declare>()
            .Select(d => d.Initializer).OfType<Expression.Call>().Single();
        Assert.Equal(CallBinding.Function, call.Binding);
        Assert.False(ConstantEvaluator.TryEvaluate(call, out _));
    }

    [Fact]
    public void WgslDisambiguatesOrdinaryFunctionsWithoutChangingEntryOrBorrowedInput()
    {
        var module = OrderedProbe(); string borrowed = WgslWriter.Emit(module);
        string text = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Contains("fn main", text); Assert.DoesNotContain("fn clamp(", text);
        Assert.Contains("clamp(", text); Assert.Contains("fn sia_wgsl_clamp_", text);
        ModuleValidator.Validate(WgslReader.Parse(text));
        Assert.Equal(borrowed, WgslWriter.Emit(module));
        Assert.Equal(SpirvWriter.Write(module, SpirvCompilationTarget.Default), SpirvWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void BuiltinSignatureValidationDoesNotUseASameNamedUserSignature()
    {
        var module = WgslReader.Parse("fn clamp(value:f32)->f32{return value;}@compute @workgroup_size(1) fn main(){let value=clamp(2.0);}");
        var declaration = module.Functions.Single(f => f.Stage is not null).Body.Statements.OfType<Statement.Declare>().First(d => d.Initializer is Expression.Call);
        var call = (Expression.Call)declaration.Initializer!;
        module.Functions.Single(f => f.Stage is not null).Body.Statements[0] = declaration with { Initializer = call with { Binding = CallBinding.Builtin } };
        Assert.Contains("requires 3", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module)).Message);
    }

    [Fact]
    public void EntryNameShadowingDiagnosesInsteadOfChangingTheEntryContract()
    {
        var input = WgslReader.Parse("@fragment fn clamp()->@builtin(frag_depth) f32{return 2.0;}");
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        Assert.Equal("clamp", prepared.Module.Functions.Single(f => f.Stage is not null).Name);
        var adapted = StructuredControlFlowLowering.Run(prepared.PhysicalLayout.Canonical);
        Assert.Contains("Entry name 'clamp'", Assert.Throws<ShaderException>(() => WgslWriter.Write(adapted, SpirvCompilationTarget.Default)).Message);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void WgslLocalShadowingRetainsBothValueAndBuiltin()
    {
        var module = SpirvOutputPolicyTests.PolicyProbe(true);
        var helper = module.Functions.Single(f => f.Name == "sia_spv_output_depth");
        helper.Body.Statements.Insert(0, new Statement.Declare("clamp", ShaderType.F32, new Expression.Literal(17f, ShaderType.F32), false));
        var prepared = WgslBuiltinNameLowering.Run(module);
        var renamed = prepared.Functions.Single(f => f.Name == helper.Name);
        Assert.StartsWith("sia_wgsl_clamp_", Assert.IsType<Statement.Declare>(renamed.Body.Statements[0]).Name);
        var builtin = Assert.IsType<Expression.Call>(Assert.Single(renamed.Body.Statements.OfType<Statement.Declare>(), d => d.Initializer is Expression.Call).Initializer);
        Assert.Equal("clamp", builtin.Function); Assert.Equal(CallBinding.Builtin, builtin.Binding);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        Assert.Equal("clamp", Assert.IsType<Statement.Declare>(helper.Body.Statements[0]).Name);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PipelineOverrideIdentityIsPreservedOrExplicitlyDiagnosed(bool numbered)
    {
        var module = SpirvOutputPolicyTests.PolicyProbe(true);
        module.Constants.Add(new("clamp", ShaderType.F32, new Expression.Literal(0f, ShaderType.F32), true, numbered ? 7u : null));
        if (!numbered) {
            Assert.Contains("pipeline constant contract", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
            Assert.Equal("clamp", Assert.Single(module.Constants).Name); return;
        }
        var prepared = WgslBuiltinNameLowering.Run(module);
        Assert.Equal(7u, Assert.Single(prepared.Constants).OverrideId);
        Assert.NotEqual("clamp", Assert.Single(prepared.Constants).Name);
        Assert.Contains("@id(7)", WgslWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void MissingResolvedFunctionDoesNotFallBackToABuiltin()
    {
        var module = SpirvOutputPolicyTests.PolicyProbe(true);
        var helper = module.Functions.Single(f => f.Name == "sia_spv_output_depth");
        var declaration = Assert.Single(helper.Body.Statements.OfType<Statement.Declare>(), d => d.Initializer is Expression.Call);
        helper.Body.Statements[helper.Body.Statements.IndexOf(declaration)] = declaration with {
            Initializer = Assert.IsType<Expression.Call>(declaration.Initializer) with { Binding = CallBinding.Function }
        };
        Assert.Contains("Unknown resolved function", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module)).Message);
    }

    [Fact]
    public void NativeRoundtripRetainsTheIndependentBuiltinAndUserEffects()
    {
        var module = SpirvReader.Parse(SpirvWriter.Write(OrderedProbe(), SpirvCompilationTarget.Default));
        ModuleValidator.Validate(module);
        var rewritten = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Contains(rewritten.Instructions, i => (Op)i.Opcode == Op.ExtInst && i.Operands[3] == 43);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("isNan")] [InlineData("spirvRayQueryGetRayFlagsKHR")] [InlineData("spirvAtomicCompareExchange")]
    public void UserFunctionNamesDoNotSelectBuiltinSpecificTargetLowering(string name)
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "fn " + name + "(value:u32)->u32{return value;}@compute @workgroup_size(1) fn main(){outputs[0]=" + name + "(inputs[0]);}");
        string text = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Contains("fn " + name + "(", text);
        ModuleValidator.Validate(WgslReader.Parse(text));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
    }
}

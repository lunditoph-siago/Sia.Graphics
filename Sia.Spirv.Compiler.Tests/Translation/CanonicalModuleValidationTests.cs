using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalModuleValidationTests
{
    private static void RefreshCalls(CanonicalModule module)
    {
        var effects = ShaderEffectAnalysis.Compute(module);
        foreach (var graph in module.Functions.Values)
            foreach (var block in graph.Blocks)
                for (int i = 0; i < block.Instructions.Count; i++)
                    if (block.Instructions[i].Operation is ValueOperation.Call call)
                        block.Instructions[i] = block.Instructions[i] with { Operation = call with { CalleeEffects = effects[call.Function] } };
    }

    [Fact]
    public void OwnedGraphValidationDoesNotReadItsCompatibilityBody()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse("@compute @workgroup_size(1) fn main(){}"));
        canonical.Declarations.Functions[0].Body.Statements.Add(new Statement.Evaluate(
            new Expression.Reference("undeclared", ShaderType.U32)));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(canonical.Declarations));
        ModuleValidator.Validate(canonical);
        Assert.Single(canonical.Declarations.Functions[0].Body.Statements);
    }

    [Theory]
    [InlineData("derivative")] [InlineData("kill")] [InlineData("barrier")]
    public void StageRulesFollowGraphHelpersThroughTheEntryCallClosure(string operation)
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(
            "fn helper(){} @compute @workgroup_size(1) fn main(){helper();}"));
        var graph = canonical.Functions["helper"]; var block = graph.Blocks[0];
        switch (operation) {
            case "derivative":
                var value = graph.Value(ShaderType.F32); var result = graph.Value(ShaderType.F32);
                block.Instructions.Add(new(value, new ValueOperation.Literal(1f)));
                block.Instructions.Add(new(result, new ValueOperation.Builtin("dpdx", [value], ShaderType.F32)));
                break;
            case "kill": block.Terminator = new ControlFlowTerminator.InvocationKill(default); break;
            default:
                canonical.Declarations.Functions.Single(f => f.Name == "main").Stage = ShaderStage.Fragment;
                block.Instructions.Add(new(null, new ValueOperation.Barrier(true, false, true, false, false)));
                break;
        }
        RefreshCalls(canonical);
        ControlFlowVerifier.Validate(canonical);
        Assert.Contains("unavailable", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(canonical)).Message);
    }

    [Fact]
    public void RecursionInTheGraphCannotBeHiddenByAnEmptyCompatibilityBody()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(
            "fn helper(){} @compute @workgroup_size(1) fn main(){helper();}"));
        canonical.Functions["helper"].Blocks[0].Instructions.Add(new(null,
            new ValueOperation.Call("helper", [], new ShaderType.Void())));
        RefreshCalls(canonical);
        Assert.Contains("Recursive", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(canonical)).Message);
    }

    [Fact]
    public void ExplicitDeferredBodiesRetainTheirSemanticValidation()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(
            "fn unused()->u32{return 3u;} @compute @workgroup_size(1) fn main(){}"));
        Assert.Contains("unused", canonical.DeferredFunctions.Keys);
        canonical.Declarations.Functions.Single(f => f.Name == "unused").Body.Statements.Clear();
        Assert.Contains("without returning", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(canonical)).Message);
    }

    [Fact]
    public void DeferredCallEffectsResolveTheOwnedGraphInsteadOfItsCompatibilityBody()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(
            "fn synchronize(){} fn unused(){synchronize();} @compute @workgroup_size(1) fn main(){synchronize();}"));
        Assert.Contains("unused", canonical.DeferredFunctions.Keys);
        canonical.Functions["synchronize"].Blocks[0].Instructions.Add(new(null,
            new ValueOperation.Barrier(true, false, true, false, false)));
        var required = ShaderBuiltinEffects.Barrier(true);
        Assert.Equal(required, ShaderEffectAnalysis.Compute(canonical)["unused"] & required);
        Assert.Empty(canonical.Declarations.Functions.Single(f => f.Name == "synchronize").Body.Statements);
    }
}

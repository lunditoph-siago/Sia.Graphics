using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalWgslTerminationTests
{
    private static Dictionary<string, string> Poison(CanonicalModule canonical)
    {
        var snapshots = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        foreach (var function in canonical.Declarations.Functions.Where(f => canonical.Functions.ContainsKey(f.Name))) {
            function.Body = new(); function.Body.Statements.Add(new Statement.Evaluate(new Expression.HelperInvocation()));
        }
        return snapshots;
    }

    [Theory] [MemberData(nameof(CanonicalInvocationTerminationTests.ContinuingCases), MemberType = typeof(CanonicalInvocationTerminationTests))]
    public void WgslTerminatingContinuingUsesOwnedGraphsAndKeepsDefinedStores(bool repeat, bool terminate, uint input, uint first, uint second, bool killed)
    {
        var canonical = CanonicalShaderPipeline.Prepare(SpirvReader.Parse(CanonicalInvocationTerminationTests.ContinuingFixture(repeat, terminate).ToBytes()));
        var snapshots = Poison(canonical);
        var kills = canonical.Functions.Values.SelectMany(g => g.Blocks).Select(b => b.Terminator).OfType<ControlFlowTerminator.InvocationKill>().ToArray();
        Assert.NotEmpty(kills);
        var lowered = WgslTerminationLowering.Run(canonical);
        Assert.DoesNotContain(lowered.Functions.Values.SelectMany(g => g.Blocks), b => b.Terminator is ControlFlowTerminator.InvocationKill or ControlFlowTerminator.Unreachable);
        foreach (var kill in kills) Assert.Contains(lowered.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Demote && i.Span == kill.Span);
        foreach (var kill in kills) Assert.Contains(lowered.Functions.Values.SelectMany(g => g.Blocks).Select(b => b.Terminator),
            t => t is ControlFlowTerminator.Return r && r.Span == kill.Span);
        var machine = new CanonicalExecution(WgslReader.Parse(WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical))), [input]);
        Assert.Equal(new[] { first, second }, machine.Run().Output); Assert.Equal(killed, machine.InvocationKilled);
        Assert.All(canonical.Functions, p => Assert.Equal(snapshots[p.Key], ControlFlowPrinter.Write(p.Value)));
        Assert.All(canonical.Functions.Values.SelectMany(g => g.Blocks).Where(b => b.Terminator is ControlFlowTerminator.InvocationKill),
            b => Assert.Contains(kills, k => k == b.Terminator));
        ModuleValidator.Validate(lowered, native: true);
    }

    [Theory]
    [InlineData(false, 0u, 2u, 8u, false)] [InlineData(true, 0u, 2u, 8u, false)]
    [InlineData(false, 1u, 0u, 88u, true)] [InlineData(true, 1u, 0u, 88u, true)]
    [InlineData(false, 2u, 2u, 8u, false)] [InlineData(true, 2u, 2u, 8u, false)]
    [InlineData(false, 3u, 2u, 88u, false)] [InlineData(true, 3u, 2u, 88u, false)]
    public void NestedContinuingRetainsPhiArgumentsAndSkippedMergeEffects(bool terminate, uint input, uint first, uint second, bool killed)
    {
        var canonical = CanonicalInvocationTerminationTests.NestedGraph(terminate); var before = Poison(canonical);
        var machine = new CanonicalExecution(WgslReader.Parse(WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical))), [input]);
        Assert.Equal(new[] { first, second }, machine.Run().Output); Assert.Equal(killed, machine.InvocationKilled);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Theory] [InlineData(0u, 7u)] [InlineData(2u, 9u)]
    public void UnreachableHelpersKeepDefinedContinuingExecutions(uint input, uint expected)
    {
        var canonical = CanonicalShaderPipeline.Prepare(SpirvReader.Parse(NativeUnreachableTests.ContinuingFixture().ToBytes()));
        var before = Poison(canonical);
        var machine = new CanonicalExecution(WgslReader.Parse(WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical))), [input]);
        Assert.Equal(new[] { expected, 13u }, machine.Run().Output);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void InternalUnreachableContinuingRelocatesBeforeCreatingAReturn()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){var n=0u;loop{if n>=1u{break;}n++;continuing{if n==9u{n=100u;}n+=0u;}}}");
        var loop = Assert.Single(module.Functions[0].Body.Statements.OfType<Statement.Loop>());
        Assert.Single(loop.Continuing.Statements.OfType<Statement.If>()).Accept.Statements.Add(new Statement.Unreachable { Span = new(57, 4) });
        var canonical = CanonicalShaderPipeline.Prepare(module); var before = Poison(canonical);
        var lowered = WgslTerminationLowering.Run(canonical); ModuleValidator.Validate(lowered, native: true);
        Assert.Contains(lowered.Functions.Values.SelectMany(g => g.Blocks).Select(b => b.Terminator), t => t is ControlFlowTerminator.Return { Span.Start: 57 });
        WgslReader.Parse(WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical)));
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ZeroAndMultipleBackedgesRetainOwnedReturnTypes(bool multiple)
    {
        var canonical = CanonicalInvocationTerminationTests.BranchingGraph(multiple); var before = Poison(canonical);
        var lowered = WgslTerminationLowering.Run(canonical); var graph = lowered.Functions["main"];
        Assert.Contains(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Demote { });
        var returned = graph.Blocks.Where(b => b.Instructions.Any(i => i.Operation is ValueOperation.Demote)).Select(b => b.Terminator).OfType<ControlFlowTerminator.Return>();
        Assert.All(returned, r => Assert.Equal(ShaderType.U32, r.Value!.Value.Type));
        WgslReader.Parse(WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical)));
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void ReturnOriginsSurviveStructuredImportMappingAndReconstruction()
    {
        var module = WgslReader.Parse("fn value()->u32{return 7u;}@compute @workgroup_size(1) fn main(){_=value();}");
        var signature = module.Functions.Single(f => f.Name == "value");
        var original = Assert.Single(signature.Body.Statements.OfType<Statement.Return>());
        signature.Body.Statements[0] = original with { Span = new(73, 2) };
        var canonical = CanonicalShaderPipeline.Prepare(module); var graph = canonical.Functions[signature.Name];
        var returned = Assert.Single(graph.Blocks.Select(b => b.Terminator).OfType<ControlFlowTerminator.Return>());
        Assert.Equal(new SourceSpan(73, 2), returned.Span);
        Assert.Equal(returned.Span, Assert.IsType<ControlFlowTerminator.Return>(returned.Map(v => v)).Span);
        var restored = StructuredControlFlowLowering.Run(graph, module);
        Assert.Equal(returned.Span, Assert.Single(restored.Body.Statements.OfType<Statement.Return>()).Span);
    }

    [Fact]
    public void UnsupportedReturnTypeFailsWithOriginAndPreservesTheBorrowedGraph()
    {
        var module = new Module(); var signature = new ShaderFunction("native") { ReturnType = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage) };
        module.Functions.Add(signature); var graph = new ControlFlowFunction(signature); var block = graph.Block(); graph.Entry = block.Id;
        var span = new SourceSpan(62, 3); block.Terminator = new ControlFlowTerminator.Unreachable(span);
        var canonical = new CanonicalModule(module, new Dictionary<string, ControlFlowFunction> { [signature.Name] = graph },
            new Dictionary<string, string>(), new HashSet<string>()); var before = Poison(canonical);
        var error = Assert.Throws<ShaderException>(() => WgslTerminationLowering.Run(canonical));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Equal(span, error.Diagnostic.Span);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void OriginalUnreachableControlMustPassUniformityBeforeTargetReturnsExist()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(2) fn main(@builtin(local_invocation_index) id:u32){if true{return;}workgroupBarrier();}");
        var function = module.Functions.Single(); var branch = Assert.IsType<Statement.If>(function.Body.Statements[0]);
        var accept = new Block(); accept.Statements.Add(new Statement.Unreachable());
        function.Body.Statements[0] = branch with { Condition = new Expression.Binary("!=", new Expression.Reference("id", ShaderType.U32), Expression.U32(0), ShaderType.Bool), Accept = accept };
        var canonical = CanonicalShaderPipeline.Prepare(module); var before = Poison(canonical);
        var error = Assert.Throws<ShaderException>(() => ShaderTargetLowering.ForWgsl(canonical));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("barrier", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void ExplicitDeferredBodyIsTheOnlyStructuredTerminationAdapter()
    {
        var module = WgslReader.Parse("fn unused()->u32{return 1u;}@compute @workgroup_size(1) fn main(){}");
        var unused = module.Functions.Single(f => f.Name == "unused"); unused.Body = new(); unused.Body.Statements.Add(new Statement.Unreachable { Span = new(71, 2) });
        var canonical = CanonicalUncalledFunctionTests.Defer(CanonicalShaderPipeline.Prepare(module), "unused"); var before = Poison(canonical);
        var lowered = WgslTerminationLowering.Run(canonical);
        Assert.Same(canonical.Functions["main"], lowered.Functions["main"]);
        Assert.Contains(lowered.DeferredFunctions, p => p.Key == "unused");
        var function = lowered.Declarations.Functions.Single(f => f.Name == "unused");
        Assert.IsType<Statement.Return>(Assert.Single(function.Body.Statements));
        Assert.IsType<Statement.Unreachable>(Assert.Single(unused.Body.Statements));
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }
}

using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalModuleTests
{
    [Theory]
    [InlineData("missing-body", "exactly one")] [InlineData("both", "exactly one")]
    [InlineData("unknown", "unknown graph")] [InlineData("signature", "signature mismatch")]
    [InlineData("terminator", "missing terminator")]
    public void ModuleVerifierDiagnosesMalformedCoverageAndStructureBeforeEffectAnalysis(string defect, string diagnostic)
    {
        var input = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}");
        var canonical = CanonicalShaderPipeline.Prepare(input);
        var graphs = canonical.Functions.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
        var deferred = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (defect) {
            case "missing-body": graphs.Clear(); break;
            case "both": deferred.Add("main", "test deferral"); break;
            case "unknown": graphs.Add("unknown", graphs["main"].Copy()); break;
            case "signature":
                graphs["main"] = new(new ShaderFunction("main") { ReturnType = ShaderType.U32 }); break;
            default: graphs["main"].Blocks[0].Terminator = null; break;
        }
        var malformed = canonical with { Functions = graphs, DeferredFunctions = deferred };
        var error = Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(malformed));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains(diagnostic, error.Message);
    }

    private const string Loop = "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>; "
        + "@compute @workgroup_size(1) fn main(){var value=0u; for(var i=0u;i<4u;i++){value+=i;} outputs[0]=value;}";

    private static ControlFlowFunction Read(Module module)
    {
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions[0], module, out var graph, out var reason), reason);
        return graph!;
    }

    [Fact]
    public void CanonicalGraphsRemainAuthoritativeUntilTheExplicitAdapter()
    {
        var input = WgslReader.Parse("@group(0) @binding(1) var<storage,read_write> outputs:array<u32>; "
            + "@compute @workgroup_size(1) fn main(){outputs[0]=7u;}");
        string before = WgslWriter.Emit(input);
        var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Empty(canonical.DeferredFunctions);
        var graph = Assert.Single(canonical.Functions).Value;
        var block = Assert.Single(graph.Blocks, b => b.Instructions.Any(i => i.Operation is ValueOperation.Literal { Value: 7u }));
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Literal { Value: 7u });
        block.Instructions[index] = block.Instructions[index] with { Operation = new ValueOperation.Literal(9u) };
        ControlFlowVerifier.Validate(graph, input);
        var adapted = StructuredControlFlowLowering.Run(canonical);
        Assert.Equal(new uint[] { 9, 0 }, new CanonicalExecution(adapted, []).Run().Output);
        Assert.Equal(before, WgslWriter.Emit(input));
        Assert.Equal(new uint[] { 7, 0 }, new CanonicalExecution(input, []).Run().Output);
    }

    [Fact]
    public void SharedPassesBorrowNativeGraphsWithoutMutatingTheirEdgesOrValues()
    {
        var input = WgslReader.Parse(Loop); var borrowed = Read(input);
        var dead = borrowed.Block(); dead.Terminator = new ControlFlowTerminator.Return();
        string before = ControlFlowPrinter.Write(borrowed);
        var traces = new List<CanonicalPassTrace>();
        var prepared = CanonicalShaderPipeline.Prepare(input, traces,
            verifyFrontend: (g, m) => ControlFlowVerifier.Validate(g, m),
            frontendGraphs: new Dictionary<string, ControlFlowFunction> { ["main"] = borrowed });
        var owned = prepared.Functions["main"];
        Assert.NotSame(borrowed, owned);
        Assert.Equal(before, ControlFlowPrinter.Write(borrowed));
        Assert.DoesNotContain(owned.Blocks, b => b.Id == dead.Id);
        Assert.Contains(owned.Blocks, b => b.Parameters.Count != 0);
        Assert.Equal(new uint[] { 6, 0 }, new CanonicalExecution(StructuredControlFlowLowering.Run(prepared), []).Run().Output);
        var promotion = Assert.Single(traces, t => t.Pass == "local-value-promotion");
        Assert.Equal(ControlFlowAnalyses.All, promotion.Preserved);
        Assert.Equal(ControlFlowAnalyses.None, promotion.Invalidated);
    }

    [Fact]
    public void PromotionPreservesDominanceAndPredecessorEdgeIdentity()
    {
        var input = WgslReader.Parse(Loop); var graph = Read(input);
        var analyses = new ControlFlowAnalysisContext(graph);
        ControlFlowVerifier.Validate(graph, input, analyses);
        var oldPredecessors = analyses.Predecessors; var oldDominance = analyses.Dominance;
        LocalValuePromotion.Run(graph, analyses);
        Assert.Same(oldDominance, analyses.Dominance);
        Assert.Same(oldPredecessors, analyses.Predecessors);
        foreach (var incoming in analyses.Predecessors.Values.SelectMany(v => v))
            Assert.Contains(incoming.Block.Terminator!.Edges, edge => ReferenceEquals(edge, incoming.Edge));
        Assert.Contains(graph.Blocks, b => b.Parameters.Count != 0);
        ControlFlowVerifier.Validate(graph, input, analyses);
        Assert.Throws<ArgumentException>(() => ControlFlowVerifier.Validate(graph.Copy(), input, analyses));
    }

    [Fact]
    public void ReplacingAnEdgePreservesDominanceButRequiresFreshPredecessorReferences()
    {
        var input = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}");
        var graph = new ControlFlowFunction(input.Functions[0]); var entry = graph.Block(); var exit = graph.Block();
        graph.Entry = entry.Id; entry.Terminator = new ControlFlowTerminator.Branch(new(exit.Id));
        exit.Terminator = new ControlFlowTerminator.Return(); var analyses = new ControlFlowAnalysisContext(graph);
        ControlFlowVerifier.Validate(graph, input, analyses);
        var before = analyses.Predecessors; var dominance = analyses.Dominance;
        var replacement = new ControlFlowEdge(exit.Id); entry.Terminator = new ControlFlowTerminator.Branch(replacement);
        analyses.Preserve(ControlFlowAnalyses.Dominance);
        Assert.NotSame(before, analyses.Predecessors); Assert.Same(dominance, analyses.Dominance);
        Assert.Same(replacement, Assert.Single(analyses.Predecessors[exit.Id]).Edge);
        ControlFlowVerifier.Validate(graph, input, analyses);
    }

    [Fact]
    public void TopologyChangesInvalidateDominanceAndPredecessorsTogether()
    {
        var input = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}");
        var graph = new ControlFlowFunction(input.Functions[0]);
        var entry = graph.Block(); var accept = graph.Block(); var reject = graph.Block(); var merge = graph.Block();
        graph.Entry = entry.Id; var condition = graph.Value(ShaderType.Bool);
        entry.Instructions.Add(new(condition, new ValueOperation.Literal(true)));
        entry.Terminator = new ControlFlowTerminator.Conditional(condition, new(accept.Id), new(reject.Id));
        accept.Terminator = new ControlFlowTerminator.Branch(new(merge.Id));
        reject.Terminator = new ControlFlowTerminator.Branch(new(merge.Id));
        merge.Terminator = new ControlFlowTerminator.Return(); graph.SelectionMerges.Add(entry.Id, merge.Id);
        var analyses = new ControlFlowAnalysisContext(graph);
        ControlFlowVerifier.Validate(graph, input, analyses);
        var oldDominance = analyses.Dominance; var oldPredecessors = analyses.Predecessors;
        Assert.DoesNotContain(accept.Id, oldDominance[merge.Id]);
        entry.Terminator = new ControlFlowTerminator.Branch(new(accept.Id));
        ControlFlowAnalysis.RemoveUnreachable(graph); graph.SelectionMerges.Clear();
        analyses.Preserve(ControlFlowAnalyses.None);
        Assert.NotSame(oldDominance, analyses.Dominance); Assert.NotSame(oldPredecessors, analyses.Predecessors);
        Assert.Contains(accept.Id, analyses.Dominance[merge.Id]); Assert.False(analyses.Predecessors.ContainsKey(reject.Id));
        ControlFlowVerifier.Validate(graph, input, analyses);
    }

    [Fact]
    public void UnmigratedBodiesAreExplicitAndDoNotBecomeAnImplicitGraphCache()
    {
        var input = WgslReader.Parse("fn unused()->u32{return 3u;} @compute @workgroup_size(1) fn main(){}");
        var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Equal("outside shader entry call graph", canonical.DeferredFunctions["unused"]);
        Assert.False(canonical.Functions.ContainsKey("unused"));
        var adapted = StructuredControlFlowLowering.Run(canonical);
        Assert.Same(input.Functions.Single(f => f.Name == "unused"), adapted.Functions.Single(f => f.Name == "unused"));
        var fresh = CanonicalShaderPipeline.Prepare(input);
        Assert.NotSame(canonical.Functions["main"], fresh.Functions["main"]);
    }

    [Fact]
    public void CallRequirementsComeFromNativeGraphsBeforeAnyStructuredAdapter()
    {
        var declarations = WgslReader.Parse("fn synchronize(){} @compute @workgroup_size(1) fn main(){synchronize();}");
        var graphs = new Dictionary<string, ControlFlowFunction>();
        foreach (var signature in declarations.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(signature, declarations, out var graph, out var reason), reason);
            graphs.Add(signature.Name, graph!);
        }
        graphs["synchronize"].Blocks[0].Instructions.Add(new(null, new ValueOperation.Barrier(true, false, true, false, false)));
        var originalCall = Assert.Single(graphs["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call);
        Assert.Equal(ShaderEffects.None, ((ValueOperation.Call)originalCall.Operation).CalleeEffects);
        var traces = new List<CanonicalPassTrace>();
        var prepared = CanonicalShaderPipeline.Prepare(declarations, traces,
            verifyFrontend: (g, m) => ControlFlowVerifier.Validate(g, m), frontendGraphs: graphs);
        var required = ShaderBuiltinEffects.Barrier(true);
        var effects = ShaderEffectAnalysis.Compute(prepared);
        Assert.Equal(required, effects["synchronize"]);
        Assert.Equal(required, effects["main"] & required);
        var call = Assert.Single(prepared.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call);
        Assert.Equal(required, ((ValueOperation.Call)call.Operation).CalleeEffects & required);
        Assert.Equal(2, traces.Count(t => t.Pass == "canonical-call-effects"));
        Assert.Equal(ControlFlowAnalyses.All, Assert.Single(traces,
            t => t.Pass == "canonical-call-effects" && t.Function == "main").Preserved);
        ControlFlowVerifier.Validate(prepared);
        Assert.Equal(ShaderEffects.None, ((ValueOperation.Call)originalCall.Operation).CalleeEffects);
    }
}

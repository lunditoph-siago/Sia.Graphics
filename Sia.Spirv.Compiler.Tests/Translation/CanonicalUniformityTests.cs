using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalUniformityTests
{
    private const string Entry = "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32)";
    private static readonly Dictionary<string, ControlFlowFunction> Structured = new(StringComparer.Ordinal);
    private static Dictionary<string, ControlFlowFunction> Graphs(Module module)
    {
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module); graphs.Add(function.Name, graph!);
        }
        return graphs;
    }

    [Fact]
    public void MutatedCanonicalPredicateIsAnalyzedInsteadOfTheOldBody()
    {
        var module = WgslReader.Parse(Entry + "{let unused=lane;if 0u==0u{workgroupBarrier();}}"); var graphs = Graphs(module);
        var graph = graphs["main"]; var entry = graph.Blocks.Single(b => b.Id == graph.Entry);
        var lane = entry.Instructions.Single(i => i.Operation is ValueOperation.Symbol { Name: "lane" }).Result!.Value;
        int index = entry.Instructions.FindIndex(i => i.Operation is ValueOperation.Binary);
        var instruction = entry.Instructions[index]; var binary = (ValueOperation.Binary)instruction.Operation;
        entry.Instructions[index] = instruction with { Operation = binary with { Left = lane } };
        Assert.Empty(UniformityAnalysis.Validate(module, Structured));
        string before = ControlFlowPrinter.Write(graph);
        var error = Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs, DiagnosticStage.WgslWrite));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.True(error.Diagnostic.Span.Length > 0);
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
    }

    [Fact]
    public void CanonicalHelperRequirementsBindSsaArgumentsAndCallSpans()
    {
        var module = WgslReader.Parse("fn gate(v:u32){if v==0u{workgroupBarrier();}}" + Entry + "{let unused=lane;gate(0u);}"); var graphs = Graphs(module);
        var block = Assert.Single(graphs["main"].Blocks);
        var lane = block.Instructions.Single(i => i.Operation is ValueOperation.Symbol { Name: "lane" }).Result!.Value;
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Call);
        var original = block.Instructions[index]; var call = (ValueOperation.Call)original.Operation;
        block.Instructions[index] = original with { Operation = call with { Arguments = [lane] } };
        Assert.Empty(UniformityAnalysis.Validate(module, Structured));
        var error = Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        Assert.Equal(original.Span, error.Diagnostic.Span); Assert.Contains("gate -> workgroupBarrier", error.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SsaPointerContentsTrackStrongAndPartialWrites(bool partial)
    {
        string body = partial ? "let unused=lane;var x=array<u32,2>();x[0]=0u;if x[0]==0u{workgroupBarrier();}"
            : "let unused=lane;var x=0u;x=0u;if x==0u{workgroupBarrier();}";
        var module = WgslReader.Parse(Entry + "{" + body + "}"); var graphs = Graphs(module); var graph = graphs["main"];
        var entry = graph.Blocks.Single(b => b.Id == graph.Entry);
        var lane = entry.Instructions.Single(i => i.Operation is ValueOperation.Symbol { Name: "lane" }).Result!.Value;
        int index = entry.Instructions.FindLastIndex(i => i.Operation is ValueOperation.Store);
        var original = entry.Instructions[index]; var store = (ValueOperation.Store)original.Operation;
        entry.Instructions[index] = original with { Operation = store with { Value = lane } };
        Assert.Empty(UniformityAnalysis.Validate(module, Structured)); Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        entry.Instructions[index] = original;
        Assert.Empty(UniformityAnalysis.Validate(module, graphs));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BlockArgumentDependenciesIncludeSelectionAndLoopIncomingValues(bool loop)
    {
        var module = WgslReader.Parse(Entry + "{workgroupBarrier();}"); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        SsaValue Add(ShaderType type, ValueOperation operation) { var value = graph.Value(type); entry.Instructions.Add(new(value, operation)); return value; }
        var lane = Add(ShaderType.U32, new ValueOperation.Symbol("lane")); var zero = Add(ShaderType.U32, new ValueOperation.Literal(0u));
        var uniform = Add(ShaderType.Bool, new ValueOperation.Literal(true)); var merged = graph.Value(ShaderType.U32);
        var header = graph.Block(); header.Parameters.Add(merged); var left = graph.Block(); var right = graph.Block(); var end = graph.Block();
        if (loop) {
            entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [zero]));
            header.Terminator = new ControlFlowTerminator.Conditional(uniform, new(left.Id), new(end.Id));
            var condition = graph.Value(ShaderType.Bool); left.Instructions.Add(new(condition, new ValueOperation.Binary("==", merged, zero)));
            var accept = graph.Block(); accept.Instructions.Add(new(null, new ValueOperation.Barrier(true, false, true, false, false), new(8, 4)));
            left.Terminator = new ControlFlowTerminator.Conditional(condition, new(accept.Id), new(right.Id));
            accept.Terminator = new ControlFlowTerminator.Branch(new(right.Id)); right.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [lane]));
            graph.Loops.Add(header.Id, new(right.Id, end.Id)); graph.SelectionMerges.Add(left.Id, right.Id);
        }
        else {
            entry.Terminator = new ControlFlowTerminator.Conditional(uniform, new(left.Id), new(right.Id));
            left.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [zero])); right.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [lane]));
            var condition = graph.Value(ShaderType.Bool); header.Instructions.Add(new(condition, new ValueOperation.Binary("==", merged, zero)));
            var accept = graph.Block(); accept.Instructions.Add(new(null, new ValueOperation.Barrier(true, false, true, false, false), new(8, 4)));
            header.Terminator = new ControlFlowTerminator.Conditional(condition, new(accept.Id), new(end.Id)); accept.Terminator = new ControlFlowTerminator.Branch(new(end.Id));
            graph.SelectionMerges.Add(entry.Id, header.Id); graph.SelectionMerges.Add(header.Id, end.Id);
        }
        end.Terminator = new ControlFlowTerminator.Return(); ControlFlowVerifier.Validate(graph, module);
        var graphs = new Dictionary<string, ControlFlowFunction> { ["main"] = graph };
        Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        foreach (var edge in graph.Blocks.SelectMany(b => b.Terminator!.Edges))
            for (int i = 0; i < edge.Arguments.Count; i++) if (edge.Arguments[i] == lane) edge.Arguments[i] = zero;
        Assert.Empty(UniformityAnalysis.Validate(module, graphs));
    }

    [Fact]
    public void CanonicalLexicalFiltersOverrideFunctionFilters()
    {
        var module = WgslReader.Parse("diagnostic(off,derivative_uniformity);@fragment fn main(@builtin(position) p:vec4f)->@location(0) f32{if p.x>0.0f{@diagnostic(off,derivative_uniformity){return dpdx(p.y);}}return 0.0f;}");
        var graphs = Graphs(module); var block = graphs["main"].Blocks.Single(b => b.Instructions.Any(i => i.Operation is ValueOperation.Builtin));
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Builtin);
        block.Instructions[index] = block.Instructions[index] with { DiagnosticFilters = [new(DiagnosticSeverity.Error, "derivative_uniformity")] };
        Assert.Empty(UniformityAnalysis.Validate(module, Structured)); Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        block.Instructions[index] = block.Instructions[index] with { DiagnosticFilters = [new(DiagnosticSeverity.Warning, "derivative_uniformity")] };
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(UniformityAnalysis.Validate(module, graphs)).Severity);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void PointerEdgeContentsConvergeThroughSelectionsAndBackedges(bool loop, bool divergent)
    {
        var module = WgslReader.Parse(Entry + "{workgroupBarrier();}"); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        SsaValue Add(ShaderType type, ValueOperation operation) { var value = graph.Value(type); entry.Instructions.Add(new(value, operation)); return value; }
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function);
        var a = Add(pointer, new ValueOperation.Local("a")); var b = Add(pointer, new ValueOperation.Local("b"));
        var lane = Add(ShaderType.U32, new ValueOperation.Symbol("lane")); var zero = Add(ShaderType.U32, new ValueOperation.Literal(0u));
        var uniform = Add(ShaderType.Bool, new ValueOperation.Literal(true));
        entry.Instructions.Add(new(null, new ValueOperation.Store(a, zero))); entry.Instructions.Add(new(null, new ValueOperation.Store(b, divergent ? lane : zero)));
        var header = graph.Block(); var merged = graph.Value(pointer); header.Parameters.Add(merged);
        var tail = graph.Block(); var end = graph.Block(); var accept = graph.Block();
        var loaded = graph.Value(ShaderType.U32); header.Instructions.Add(new(loaded, new ValueOperation.Load(merged)));
        var condition = graph.Value(ShaderType.Bool); header.Instructions.Add(new(condition, new ValueOperation.Binary("==", loaded, zero)));
        header.Terminator = new ControlFlowTerminator.Conditional(condition, new(accept.Id), new(tail.Id));
        accept.Instructions.Add(new(null, new ValueOperation.Barrier(true, false, true, false, false), new(8, 4)));
        accept.Terminator = new ControlFlowTerminator.Branch(new(tail.Id)); graph.SelectionMerges.Add(header.Id, tail.Id);
        if (loop) {
            var outer = graph.Block(); outer.Terminator = new ControlFlowTerminator.Conditional(uniform, new(header.Id, [a]), new(end.Id));
            entry.Terminator = new ControlFlowTerminator.Branch(new(outer.Id)); tail.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [b]));
            // This graph's natural loop starts at header and has no exit; end belongs
            // to the outer uniform branch and does not fake an iteration join.
            graph.Loops.Add(header.Id, new(tail.Id, null));
        }
        else {
            var left = graph.Block(); var right = graph.Block();
            entry.Terminator = new ControlFlowTerminator.Conditional(uniform, new(left.Id), new(right.Id));
            left.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [a])); right.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [b]));
            tail.Terminator = new ControlFlowTerminator.Branch(new(end.Id)); graph.SelectionMerges.Add(entry.Id, header.Id);
        }
        end.Terminator = new ControlFlowTerminator.Return(); ControlFlowVerifier.Validate(graph, module);
        var graphs = new Dictionary<string, ControlFlowFunction> { ["main"] = graph };
        if (divergent) Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        else Assert.Empty(UniformityAnalysis.Validate(module, graphs));
    }

    [Fact]
    public void NativeMemoryFenceDoesNotAcquireAnExecutionRequirement()
    {
        var module = WgslReader.Parse(Entry + "{if lane==0u{}}"); var graphs = Graphs(module); var graph = graphs["main"];
        var entry = graph.Blocks.Single(b => b.Id == graph.Entry); var branch = (ControlFlowTerminator.Conditional)entry.Terminator!;
        var accept = graph.Blocks.Single(b => b.Id == branch.Accept.Target);
        accept.Instructions.Add(new(null, new ValueOperation.Barrier(false, true, false, false, false), new(8, 4)));
        Assert.Empty(UniformityAnalysis.Validate(module, graphs));
        accept.Instructions[0] = accept.Instructions[0] with { Operation = new ValueOperation.Barrier(true, true, false, false, false) };
        Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
    }

    [Theory]
    [InlineData("loop{if lane==0u{continue;}continuing{workgroupBarrier();}}", false)]
    [InlineData("loop{if lane==0u{continue;}workgroupBarrier();}", false)]
    [InlineData("loop{if lane==0u{let x=1u;}workgroupBarrier();}", true)]
    [InlineData("loop{if lane==0u{return;}break;}workgroupBarrier();", false)]
    [InlineData("var j=0u;loop{j++;if j==lane+1u{break;}}workgroupBarrier();", true)]
    [InlineData("loop{if lane==0u{break;}break;}workgroupBarrier();", true)]
    public void LoopReconvergenceRespectsIterationAndReturnBoundaries(string body, bool accepted)
    {
        // Parse with a filterable collective requirement, then restore Error to compare
        // structured and canonical analyses on identical typed IR, including rejected cases.
        string derivative = body.Replace("lane", "u32(p.x)", StringComparison.Ordinal).Replace("workgroupBarrier();", "_=dpdx(p.y);", StringComparison.Ordinal);
        var source = WgslReader.Parse("diagnostic(off,derivative_uniformity);@fragment fn f(@builtin(position) p:vec4f){" + derivative + "}");
        source.DiagnosticFilters.Clear();
        if (accepted) {
            Assert.Empty(UniformityAnalysis.Validate(source, Structured)); Assert.Empty(UniformityAnalysis.Validate(source, Graphs(source)));
        }
        else {
            Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(source, Structured));
            Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(source, Graphs(source)));
        }
    }

    [Fact]
    public void UnsupportedCanonicalFamiliesRetainAnExplicitDeferral()
    {
        var module = WgslReader.Parse("enable wgpu_ray_query;fn handle(q:ptr<function,ray_query>){}@compute @workgroup_size(1) fn main(){var q:ray_query;handle(&q);}");
        var deferrals = new List<CanonicalDeferral>(); Assert.Empty(UniformityAnalysis.Validate(module, deferrals: deferrals));
        Assert.Equal("main", Assert.Single(deferrals).Function);
    }
}

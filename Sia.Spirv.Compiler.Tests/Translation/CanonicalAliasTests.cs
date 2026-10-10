using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalAliasTests
{
    private const string Source = "fn write(a:ptr<function,u32>,b:ptr<function,u32>){*a=1u;}@compute @workgroup_size(1) fn main(){var a=0u;var b=0u;write(&a,&b);}";
    private static Dictionary<string, ControlFlowFunction> Graphs(Module module)
    {
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var f in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(f, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
            graphs.Add(f.Name, graph!);
        }
        return graphs;
    }

    [Fact]
    public void CanonicalCallsAreAnalyzedInsteadOfTheSignaturesOldBody()
    {
        var module = WgslReader.Parse(Source); var graphs = Graphs(module);
        var block = Assert.Single(graphs["main"].Blocks);
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Call);
        var instruction = block.Instructions[index]; var call = (ValueOperation.Call)instruction.Operation;
        block.Instructions[index] = instruction with { Operation = call with { Arguments = [call.Arguments[0], call.Arguments[0]] } };
        PointerAliasAnalysis.ValidateSource(module); // The source body still passes.
        var error = Assert.Throws<ShaderException>(() => PointerAliasAnalysis.Validate(module, graphs));
        Assert.Contains("Pointer alias violation", error.Message); Assert.True(error.Diagnostic.Span.Length > 0);
    }

    private static Dictionary<string, ControlFlowFunction> Merged(Module module, bool loop, bool overlap)
    {
        var graphs = Graphs(module); var graph = new ControlFlowFunction(module.Functions.Single(f => f.Name == "main"));
        graphs["main"] = graph;
        var entry = graph.Block(); graph.Entry = entry.Id;
        SsaValue Add(ShaderType type, ValueOperation operation) {
            var value = graph.Value(type); entry.Instructions.Add(new(value, operation)); return value;
        }
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function);
        var a = Add(pointer, new ValueOperation.Local("a")); var b = Add(pointer, new ValueOperation.Local("b"));
        var c = Add(pointer, new ValueOperation.Local("c")); var condition = Add(ShaderType.Bool, new ValueOperation.Literal(true));
        var merged = graph.Value(pointer); var effects = ShaderEffectAnalysis.Compute(module)["write"];
        var span = new SourceSpan(12, 3);
        if (loop) {
            var header = graph.Block(); var body = graph.Block(); var tail = graph.Block(); var exit = graph.Block();
            header.Parameters.Add(merged); entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [a]));
            header.Terminator = new ControlFlowTerminator.Conditional(condition, new(body.Id), new(exit.Id));
            body.Instructions.Add(new(null, new ValueOperation.Call("write", [merged, overlap ? b : c], new ShaderType.Void(), CalleeEffects: effects), span));
            body.Terminator = new ControlFlowTerminator.Branch(new(tail.Id));
            tail.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [b]));
            exit.Terminator = new ControlFlowTerminator.Return(); graph.Loops.Add(header.Id, new(tail.Id, exit.Id));
        }
        else {
            var left = graph.Block(); var right = graph.Block(); var merge = graph.Block(); merge.Parameters.Add(merged);
            entry.Terminator = new ControlFlowTerminator.Conditional(condition, new(left.Id), new(right.Id));
            left.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [a]));
            right.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [b]));
            merge.Instructions.Add(new(null, new ValueOperation.Call("write", [merged, overlap ? a : c], new ShaderType.Void(), CalleeEffects: effects), span));
            merge.Terminator = new ControlFlowTerminator.Return(); graph.SelectionMerges.Add(entry.Id, merge.Id);
        }
        ControlFlowVerifier.Validate(graph, module); return graphs;
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void PointerEdgeRootsConvergeAcrossSelectionsAndLoopBackedges(bool loop, bool overlap)
    {
        var module = WgslReader.Parse(Source); var graphs = Merged(module, loop, overlap);
        string before = ControlFlowPrinter.Write(graphs["main"]);
        if (overlap) {
            var error = Assert.Throws<ShaderException>(() => PointerAliasAnalysis.Validate(module, graphs));
            Assert.Contains("Pointer alias violation", error.Message); Assert.Equal(new SourceSpan(12, 3), error.Diagnostic.Span);
        }
        else PointerAliasAnalysis.Validate(module, graphs);
        Assert.Equal(before, ControlFlowPrinter.Write(graphs["main"]));
    }

    [Fact]
    public void CanonicalTransitiveGlobalReadsParticipateInTheSameAliasRules()
    {
        var module = WgslReader.Parse("var<private> slot:u32;var<private> other:u32;fn read()->u32{return slot;}fn write(p:ptr<private,u32>){*p=read();}@compute @workgroup_size(1) fn main(){write(&other);}");
        var graphs = Graphs(module); var block = Assert.Single(graphs["main"].Blocks);
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Symbol { Name: "other" });
        block.Instructions[index] = block.Instructions[index] with { Operation = new ValueOperation.Symbol("slot") };
        Assert.Contains("Pointer alias violation", Assert.Throws<ShaderException>(() => PointerAliasAnalysis.Validate(module, graphs)).Message);
    }

    [Fact]
    public void UnreachableSourceCallsStillHaveStaticAliasValidation()
    {
        string invalid = Source.Replace("write(&a,&b);", "return;write(&a,&a);", StringComparison.Ordinal);
        Assert.Contains("Pointer alias violation", Assert.Throws<ShaderException>(() => WgslReader.Parse(invalid)).Message);
    }

    [Fact]
    public void QueryHandlesHaveCanonicalAliasAnalysisWithoutDeferral()
    {
        var module = WgslReader.Parse("enable wgpu_ray_query;fn handle(q:ptr<function,ray_query>){}@compute @workgroup_size(1) fn main(){var q:ray_query;handle(&q);}");
        var deferrals = new List<CanonicalDeferral>(); PointerAliasAnalysis.Validate(module, deferrals: deferrals);
        Assert.Empty(deferrals);
    }
}

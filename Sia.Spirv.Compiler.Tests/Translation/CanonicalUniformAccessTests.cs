using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalUniformAccessTests
{
    private static ControlFlowFunction Read(Module input, string name = "main")
    {
        Assert.True(StructuredControlFlowReader.TryRead(input.Functions.Single(f => f.Name == name), input, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, input); return graph!;
    }

    private static SpirvPhysicalLayout Policy(Module input) => SpirvPhysicalLayoutLowering.Prepare(input) with {
        Module = input, ControlFlow = FrozenDictionary<string, SpirvFunctionControlFlow>.Empty,
        DeferredControlFlow = FrozenDictionary<string, string>.Empty
    };

    [Theory]
    [InlineData(0u, false)] [InlineData(3u, false)] [InlineData(48u, true)] [InlineData(55u, true)]
    public void NativeLeafOperandsAndCallerResultOriginSurviveDirectGraphLowering(uint flags, bool vulkan)
    {
        var input = UniformMemoryTests.Fixture(flags, vulkan: vulkan); var original = Read(input); var graph = original.Copy();
        var block = Assert.Single(graph.Blocks); int position = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Load);
        var instruction = block.Instructions[position] with { Span = new(17, 3), DiagnosticFilters = [new(DiagnosticSeverity.Warning, "derivative_uniformity")] };
        block.Instructions[position] = instruction; var native = ((ValueOperation.Load)instruction.Operation).MemoryAccess;
        var edges = graph.Blocks.SelectMany(b => b.Terminator!.Edges).ToArray(); string before = ControlFlowPrinter.Write(original);
        var policy = Policy(input); var graphs = new Dictionary<string, ControlFlowFunction> { ["main"] = graph };
        var lowered = SpirvUniformAccessLowering.Run(policy, graphs);
        var call = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Result == instruction.Result);
        Assert.IsType<ValueOperation.Call>(call.Operation); Assert.Equal(instruction.Span, call.Span); Assert.Equal(instruction.DiagnosticFilters, call.DiagnosticFilters);
        var helper = graphs[((ValueOperation.Call)call.Operation).Function];
        var leaf = Assert.Single(helper.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Load);
        Assert.Equal(native, ((ValueOperation.Load)leaf.Operation).MemoryAccess);
        Assert.Equal(instruction.Span, leaf.Span); Assert.Equal(instruction.DiagnosticFilters, leaf.DiagnosticFilters);
        Assert.Empty(helper.Signature.Body.Statements); Assert.Equal(edges, graph.Blocks.SelectMany(b => b.Terminator!.Edges));
        Assert.Equal(before, ControlFlowPrinter.Write(original)); Assert.Same(input.Functions[0], lowered.Module.Functions[0]);
        Assert.NotEqual(input.Globals[0].Type, lowered.Module.Globals[0].Type);
        ModuleValidator.Validate(new CanonicalModule(lowered.Module, graphs, new Dictionary<string, string>(), new HashSet<string> { "main" }));
    }

    internal const string EffectfulAliasSource = """
        struct Data{matrix:mat2x2f,tail:f32,}
        @group(0) @binding(0) var<uniform> data:Data;
        @group(0) @binding(1) var<storage,read_write> output:array<f32>;
        @group(0) @binding(2) var<storage,read_write> counter:array<u32>;
        fn next()->u32{counter[0]+=1u;return counter[1];}
        @compute @workgroup_size(1) fn main(){let p=&data.matrix[next()];counter[1]=1u;output[0]=(*p)[1];output[1]=(*p)[0];}
        """;

    [Fact]
    public void DynamicAliasUsesCapturedEffectfulIndexAndPureHelperPhiValues()
    {
        var input = WgslReader.Parse(EffectfulAliasSource); var original = Read(input); var graph = original.Copy();
        var captured = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call { Function: "next" });
        var oldStores = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Store).Select(i => ((ValueOperation.Store)i.Operation).Pointer.Id).ToArray();
        var graphs = input.Functions.ToDictionary(f => f.Name, f => f.Name == "main" ? graph : Read(input, f.Name));
        var lowered = SpirvUniformAccessLowering.Run(Policy(input), graphs);
        var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
        Assert.Single(instructions, i => i.Operation is ValueOperation.Call { Function: "next" });
        var reads = instructions.Where(i => i.Operation is ValueOperation.Call c && c.Function.StartsWith("sia_spv_uniform_read_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, reads.Length);
        foreach (var instruction in reads) {
            var call = (ValueOperation.Call)instruction.Operation;
            var index = Assert.Single(call.Arguments); var definitions = instructions.Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
            while (definitions[index.Id] is ValueOperation.Let let) index = let.Value;
            Assert.Equal(captured.Result, index);
            var helper = graphs[call.Function]; var selection = Assert.Single(helper.Blocks, b => b.Terminator is ControlFlowTerminator.Switch);
            var branch = (ControlFlowTerminator.Switch)selection.Terminator!; Assert.Equal(2, branch.Cases.Count);
            var joined = helper.Blocks.Single(b => b.Id == helper.SelectionMerges[selection.Id]); Assert.Single(joined.Parameters);
            Assert.Equal(3, helper.Blocks.SelectMany(b => b.Terminator!.Edges).Count(e => e.Target == joined.Id));
            Assert.DoesNotContain(helper.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call or ValueOperation.Local or ValueOperation.Store);
            Assert.Equal(2, helper.Blocks.SelectMany(b => b.Instructions).Count(i => i.Operation is ValueOperation.Load));
            Assert.Empty(helper.Signature.Body.Statements);
        }
        Assert.Equal(oldStores, instructions.Where(i => i.Operation is ValueOperation.Store).Select(i => ((ValueOperation.Store)i.Operation).Pointer.Id));
        ModuleValidator.Validate(new CanonicalModule(lowered.Module, graphs, new Dictionary<string, string>(), new HashSet<string> { "main" }));
    }

    [Fact]
    public void ContinuingAliasAndLoopTopologyRemainInTheCallerGraph()
    {
        var input = WgslReader.Parse(SpirvUniformLegalizationTests.ContinuingSource); var original = Read(input); var graph = original.Copy();
        var edges = graph.Blocks.SelectMany(b => b.Terminator!.Edges).ToArray(); var loops = graph.Loops.ToArray(); string before = ControlFlowPrinter.Write(original);
        var graphs = new Dictionary<string, ControlFlowFunction> { ["main"] = graph };
        var lowered = SpirvUniformAccessLowering.Run(Policy(input), graphs);
        Assert.Equal(edges, graph.Blocks.SelectMany(b => b.Terminator!.Edges)); Assert.Equal(loops, graph.Loops.ToArray());
        Assert.Equal(before, ControlFlowPrinter.Write(original));
        Assert.Equal(3, graph.Blocks.SelectMany(b => b.Instructions).Count(i => i.Operation is ValueOperation.Store));
        ModuleValidator.Validate(new CanonicalModule(lowered.Module, graphs, new Dictionary<string, string>(), new HashSet<string> { "main" }));
    }

    [Fact]
    public void UniformPointerParameterShadowDoesNotBecomeTheGlobalResource()
    {
        var input = WgslReader.Parse("struct Data{matrix:mat2x2f,tail:f32,} @group(0) @binding(0) var<uniform> data:Data; fn read(data:ptr<uniform,Data>)->f32{return (*data).matrix[0][1];}");
        var original = Read(input, "read"); var graph = original.Copy(); string before = ControlFlowPrinter.Write(graph);
        var graphs = new Dictionary<string, ControlFlowFunction> { ["read"] = graph };
        var lowered = SpirvUniformAccessLowering.Run(Policy(input), graphs);
        Assert.Equal(before, ControlFlowPrinter.Write(graph)); Assert.Single(graphs);
        Assert.Single(lowered.Module.Functions); ControlFlowVerifier.Validate(graph, lowered.Module);
    }
}

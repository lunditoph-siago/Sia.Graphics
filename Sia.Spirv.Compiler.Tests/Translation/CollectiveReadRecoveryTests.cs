using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CollectiveReadRecoveryTests
{
    internal const string UniformLoop = """
        var<workgroup> data:array<u32,4>;
        @group(0) @binding(0) var<storage,read_write> output:array<u32>;
        fn collective(index:u32)->u32{return workgroupUniformLoad(&data[index]);}
        @compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32){
            data[lane]=lane+10u;
            let value=collective(0u);
            loop{continuing{break if collective(0u)==10u;}}
            if value==10u{workgroupBarrier();}
            output[lane]=value+lane;
        }
        """;

    internal const string DivergentRead = """
        var<workgroup> data:array<u32,4>;
        @group(0) @binding(0) var<storage,read_write> output:array<u32>;
        @compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32){
            data[lane]=lane+10u;
            workgroupBarrier();let first=data[lane];workgroupBarrier();
            workgroupBarrier();let second=data[first%4u];workgroupBarrier();
            output[lane]=second;
        }
        """;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OrderedNativeLoopsRetainUniformResultsAndBorrowTheirInput(bool raw)
    {
        var binary = raw ? SpirvSynchronizationTests.RawOrderedBinary()
            : SpirvWriter.Write(WgslReader.Parse(SpirvSynchronizationTests.OrderedSource), SpirvCompilationTarget.Default);
        var input = SpirvReader.Parse(binary);
        var graphs = Graphs(input); var before = Dump(graphs);
        Assert.Empty(UniformityAnalysis.Validate(input,graphs));
        string wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.Contains("workgroupUniformLoad",wgsl);
        Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(wgsl)));
        Assert.Equal(before,Dump(graphs));
        Assert.Equal(wgsl,WgslWriter.Write(input, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void HelperAndLoopRequirementsAreProvedForMultipleInvocations()
    {
        var input = SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(UniformLoop), SpirvCompilationTarget.Default));
        Assert.Empty(UniformityAnalysis.Validate(input));
        string wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.Contains("workgroupUniformLoad",wgsl);
        Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(wgsl)));
    }

    [Fact]
    public void SourceWgslStillUsesItsStandardUniformityRules()
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse(
            "var<workgroup> data:u32;@compute @workgroup_size(4) fn main(){workgroupBarrier();let v=data;workgroupBarrier();if v==0u{workgroupBarrier();}}"));
    }

    [Theory]
    [InlineData("read_at(lane)")]
    [InlineData("read_at(0u)+read_at(lane)")]
    public void DivergentActualHelperArgumentsDoNotGainAUniformRead(string calls)
    {
        string source = "var<workgroup> data:array<u32,4>;@group(0) @binding(0) var<storage,read_write> output:array<u32>;"
            + "fn read_at(i:u32)->u32{workgroupBarrier();let v=data[i];workgroupBarrier();return v;}"
            + "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32){output[lane]="+calls+";}";
        var input = SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default));
        string wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.DoesNotContain("workgroupUniformLoad",wgsl);
        Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(wgsl)));
    }

    [Fact]
    public void RecoveryFailurePropagatesToDependentReadCandidates()
    {
        var input = SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(DivergentRead), SpirvCompilationTarget.Default));
        string wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.DoesNotContain("workgroupUniformLoad",wgsl);
        Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(wgsl)));
    }

    [Fact]
    public void UniformActualHelperArgumentsRecoverTheNativeRead()
    {
        const string source = "var<workgroup> data:array<u32,4>;@group(0) @binding(0) var<storage,read_write> output:array<u32>;"
            + "fn read_at(i:u32)->u32{workgroupBarrier();let v=data[i];workgroupBarrier();return v;}"
            + "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32){output[lane]=read_at(0u);}";
        string wgsl = ShaderTranslator.SpirvToWgsl(SpirvWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default), SpirvCompilationTarget.Default);
        Assert.Contains("workgroupUniformLoad",wgsl);
        Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(wgsl)));
    }

    [Theory]
    [InlineData("data[lane]=lane;")]
    [InlineData("_=atomicAdd(&counter,1u);")]
    [InlineData("let second=data[1];_=second;")]
    [InlineData("_=opaque();")]
    public void InterveningEffectsAreNotMovedAcrossTheRead(string effect)
    {
        string source = "var<workgroup> data:array<u32,4>;var<workgroup> counter:atomic<u32>;"
            + "@group(0) @binding(0) var<storage,read_write> output:array<u32>;fn opaque()->u32{return 7u;}"
            + "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32){workgroupBarrier();let first=data[0];"
            + effect+"workgroupBarrier();output[lane]=first;}";
        var input = SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default));
        Assert.Same(input,CollectiveReadRecovery.Run(input));
        string wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.DoesNotContain("workgroupUniformLoad",wgsl);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeAccessAndNonDefaultBarrierOperandsPreventRecovery(bool access)
    {
        var input = SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(
            "var<workgroup> data:u32;@group(0) @binding(0) var<storage,read_write> output:u32;@compute @workgroup_size(4) fn main(){output=workgroupUniformLoad(&data);}"), SpirvCompilationTarget.Default));
        var graphs = Graphs(input);
        var graph = graphs.Values.Single(g => g.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Load { Pointer.Type: ShaderType.Pointer { Space:AddressSpace.Workgroup } }));
        var block = graph.Blocks.Single(b => b.Instructions.Any(i => i.Operation is ValueOperation.Load { Pointer.Type: ShaderType.Pointer { Space:AddressSpace.Workgroup } }));
        int index = block.Instructions.FindIndex(i => access ? i.Operation is ValueOperation.Load { Pointer.Type:ShaderType.Pointer { Space:AddressSpace.Workgroup } } : i.Operation is ValueOperation.Barrier);
        var instruction = block.Instructions[index];
        block.Instructions[index] = instruction with { Operation = access
            ? ((ValueOperation.Load)instruction.Operation) with { MemoryAccess = new(1u) }
            : ((ValueOperation.Barrier)instruction.Operation) with { NativeMemory = new(2,272,2) } };
        ControlFlowVerifier.Validate(graph,input);
        string before = Dump(graphs);
        Assert.Same(graphs,CollectiveReadRecovery.Recover(input,graphs));
        Assert.Equal(before,Dump(graphs));
    }

    [Fact]
    public void GraphCopiesOwnEdgesAndRetainAllocationCounters()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32){if lane==0u{return;}}");
        var input = Graphs(module)["main"]; string before = ControlFlowPrinter.Write(input);
        var copy = input.Copy(); var edge = copy.Blocks.SelectMany(b => b.Terminator!.Edges).First();
        edge.Arguments.Add(copy.Value(ShaderType.U32));
        int previousBlock = input.Blocks.Max(b => b.Id); int previousValue = input.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value))).Max(v => v.Id);
        Assert.True(copy.Block().Id > previousBlock); Assert.True(copy.Value(ShaderType.U32).Id > previousValue);
        Assert.Equal(before,ControlFlowPrinter.Write(input));
    }

    private static Dictionary<string,ControlFlowFunction> Graphs(Module module)
    {
        var result = new Dictionary<string,ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function,module,out var graph,out var reason),reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!,module);
            result.Add(function.Name,graph!);
        }
        return result;
    }
    private static string Dump(IReadOnlyDictionary<string,ControlFlowFunction> graphs)
        => string.Join("\n",graphs.OrderBy(p => p.Key,StringComparer.Ordinal).Select(p => ControlFlowPrinter.Write(p.Value)));
}

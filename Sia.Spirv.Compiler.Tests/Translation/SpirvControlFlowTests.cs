using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvControlFlowTests
{
    private const string Storage = "@group(0) @binding(0) var<storage,read_write> output:array<u32>;";
    private const string LoopSource = Storage + "@compute @workgroup_size(1) fn main(){var i=0u;var sum=0u;loop{if i==5u{break;}i=i+1u;if i==2u{continue;}switch i{case 1u,3u:{sum=sum+i;}default:{sum=sum+10u;}}}output[0]=sum;}";

    public static IReadOnlyList<string> Sources => new string[] {
        Storage + "@compute @workgroup_size(1) fn main(){output[0]=7u;}",
        Storage + "fn side()->bool{output[0]=output[0]+1u;return true;} @compute @workgroup_size(1) fn main(){let x=false && side();let y=true || side();output[1]=select(0u,1u,x||y);}",
        Storage + "fn pick(x:u32)->u32{if x==0u{return 3u;}else{return 5u;}} @compute @workgroup_size(1) fn main(){output[0]=pick(0u);}",
        Storage + "fn pick()->u32{loop{return 9u;}} @compute @workgroup_size(1) fn main(){output[0]=pick();}",
        Storage + "@compute @workgroup_size(1) fn main(){loop{output[0]=9u;break;}output[1]=11u;}",
        Storage + "@compute @workgroup_size(1) fn main(){var i=0u;var sum=0u;loop{if i==5u{break;}i=i+1u;if i==2u{continue;}switch i{case 1u,3u:{sum=sum+i;}default:{sum=sum+10u;}}}output[0]=sum;}",
        Storage + "@compute @workgroup_size(1) fn main(){var i=0u;loop{var j=0u;loop{j=j+1u;if j==2u{break;}}output[i]=j;continuing{i=i+1u;break if i==3u;}}}",
        Storage + "fn pick(x:i32)->u32{switch x{case -1:{return 7u;}case 0,2:{return 9u;}default:{return 11u;}}} @compute @workgroup_size(1) fn main(){output[0]=pick(-1);}",
    };

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void PreparedCfgSerializesAndRoundtripsWithoutReconstructingTheFunctionBody(int fixture)
    {
        var source = Sources[fixture];
        var input = WgslReader.Parse(source);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        Assert.Empty(prepared.PhysicalLayout.DeferredControlFlow);
        Assert.Equal(prepared.Module.Functions.Count, prepared.PhysicalLayout.ControlFlow.Count);
        var before = SpirvWriter.Emit(prepared).ToBytes();
        var read = SpirvReader.Parse(before); ModuleValidator.ValidateNative(read);
        WgslReader.Parse(WgslWriter.Write(read));
        foreach (var function in prepared.Module.Functions) {
            function.Body.Statements.Clear(); function.Body.Statements.Add(new Statement.Unreachable());
        }
        Assert.Equal(before, SpirvWriter.Emit(prepared).ToBytes());
    }

    [Fact]
    public void LoopLegalizationOwnsHeaderSplittingAndPreservesBorrowedGraph()
    {
        var module = WgslReader.Parse(LoopSource);
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(), module, out var graph, out var reason), reason);
        Sia.Spirv.Compiler.Translation.Proc.ControlFlowAnalysis.RemoveUnreachable(graph!);
        var before = ControlFlowPrinter.Write(graph!);
        var prepared = SpirvControlFlowLowering.Prepare(graph!, module);
        Assert.Equal(before, ControlFlowPrinter.Write(graph!));
        ControlFlowVerifier.Validate(prepared.Graph, module);
        Assert.Equal(prepared.BlockOrder.Count, prepared.BlockOrder.Distinct().Count());
        Assert.Equal(prepared.Graph.Blocks.Count + prepared.StructuralTargets.Count, prepared.BlockOrder.Count);
        foreach (int header in prepared.Loops.Keys) {
            var block = prepared.Graph.Blocks.Single(b => b.Id == header);
            Assert.Empty(block.Instructions); Assert.IsType<ControlFlowTerminator.Branch>(block.Terminator);
            Assert.False(prepared.SelectionMerges.ContainsKey(header));
        }
    }

    [Fact]
    public void LoopCarriedValuesAndShortCircuitResultsUseNativePhi()
    {
        var module = WgslReader.Parse(LoopSource);
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true);
        var binary = SpirvWriter.Emit(prepared);
        Assert.Contains(binary.Instructions, i => i.Opcode == (ushort)Op.Phi);
        Assert.All(binary.Instructions.Where(i => i.Opcode == (ushort)Op.Phi), i => Assert.True(i.Operands.Length >= 4 && i.Operands.Length % 2 == 0));
        Assert.Contains(binary.Instructions, i => i.Opcode == (ushort)Op.Phi && i.Operands.Length >= 6);
        ModuleValidator.ValidateNative(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory] [InlineData(3)] [InlineData(4)]
    public void TerminatingLoopsRetainAnUnreachableStructuralBackedge(int fixture)
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Sources[fixture]), null, true, true, true);
        var loops = prepared.PhysicalLayout.ControlFlow.Values.Where(p => p.Loops.Count != 0).ToArray();
        Assert.NotEmpty(loops);
        foreach (var function in loops) {
            Assert.NotEmpty(function.StructuralBackedges);
            foreach (var edge in function.StructuralBackedges) {
                Assert.Contains(edge.Key, function.StructuralTargets);
                Assert.Equal(edge.Key, function.Loops[edge.Value].Continuing);
                Assert.DoesNotContain(function.Graph.Blocks, b => b.Id == edge.Key);
            }
        }
    }
}

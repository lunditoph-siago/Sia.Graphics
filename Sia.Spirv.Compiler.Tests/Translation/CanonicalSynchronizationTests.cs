using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalSynchronizationTests
{
    private static ControlFlowFunction Read(Module module, string name = "main")
    {
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(f => f.Name == name), module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); LocalValuePromotion.Run(graph!);
        ControlFlowVerifier.Validate(graph!, module); return graph!;
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public void UniformReadExpansionPreservesResultAddressAndQualifiedOperands(bool atomic, bool vulkan)
    {
        var input = WgslReader.Parse("var<workgroup> value:"+(atomic ? "atomic<u32>" : "u32")+"; fn main()->u32{return workgroupUniformLoad(&value);}");
        input.VulkanMemoryModel = vulkan;
        input.Globals[0] = input.Globals[0] with { MemoryDecorations = MemoryDecorations.Volatile };
        var original = Read(input); var graph = original.Copy(); var block = Assert.Single(graph.Blocks);
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Builtin { Function: "workgroupUniformLoad" });
        var instruction = block.Instructions[index]; var call = (ValueOperation.Builtin)instruction.Operation;
        block.Instructions[index] = instruction with { DiagnosticFilters = [new(DiagnosticSeverity.Warning,"derivative_uniformity")],
            Operation = call with { AtomicMemory = atomic ? new(2,258) : null, MemoryAccess = atomic ? null : new(2,Alignment:4) } };
        string before = ControlFlowPrinter.Write(original);
        var result = instruction.Result; var pointer = call.Arguments.Single(); var terminator = block.Terminator;
        SpirvMemoryAccessLowering.Run(graph,input); SpirvSynchronizationLowering.Run(graph,input);
        var expanded = block.Instructions.Skip(index).Take(3).ToArray(); Assert.Equal(3,expanded.Length);
        Assert.Equal(new SpirvBarrierMemory(2,264,2),Assert.IsType<ValueOperation.Barrier>(expanded[0].Operation).NativeMemory);
        Assert.Equal(new SpirvBarrierMemory(2,264,2),Assert.IsType<ValueOperation.Barrier>(expanded[2].Operation).NativeMemory);
        Assert.Null(expanded[0].Result); Assert.Null(expanded[2].Result); Assert.Equal(result,expanded[1].Result);
        Assert.All(expanded,i => { Assert.Equal(instruction.Span,i.Span); Assert.Equal(DiagnosticSeverity.Warning,Assert.Single(i.DiagnosticFilters).Severity); });
        if (atomic) {
            var read = Assert.IsType<ValueOperation.Builtin>(expanded[1].Operation);
            Assert.Equal("atomicLoad",read.Function); Assert.Equal(pointer,Assert.Single(read.Arguments));
            Assert.Equal(new SpirvAtomicMemory(2,vulkan ? 33026u : 258u),read.AtomicMemory);
        }
        else {
            var read = Assert.IsType<ValueOperation.Load>(expanded[1].Operation); Assert.Equal(pointer,read.Pointer);
            Assert.Equal(vulkan ? new SpirvMemoryAccess(51,Alignment:4,VisibleScope:2) : new SpirvMemoryAccess(2,Alignment:4),read.MemoryAccess);
        }
        Assert.Same(terminator,block.Terminator); Assert.Equal(before,ControlFlowPrinter.Write(original));
        ControlFlowVerifier.Validate(graph,input);
        string once = ControlFlowPrinter.Write(graph); SpirvSynchronizationLowering.Run(graph,input);
        Assert.Equal(once,ControlFlowPrinter.Write(graph));
    }

    [Theory]
    [InlineData("workgroupBarrier",2u,264u)] [InlineData("storageBarrier",2u,72u)]
    [InlineData("textureBarrier",2u,2056u)] [InlineData("subgroupBarrier",3u,264u)]
    public void BuiltinBarriersBecomeTypedOperationsWithExactOperands(string name,uint scope,uint semantics)
    {
        var input = WgslReader.Parse("@compute @workgroup_size(1) fn main(){"+name+"();}");
        var graph = Read(input); SpirvSynchronizationLowering.Run(graph,input);
        var barrier = Assert.IsType<ValueOperation.Barrier>(Assert.Single(graph.Blocks.SelectMany(b => b.Instructions)).Operation);
        Assert.Equal(new SpirvBarrierMemory(scope,semantics,scope),barrier.NativeMemory);
        ControlFlowVerifier.Validate(graph,input);
    }

    [Fact]
    public void OrderedUniformReadsKeepAllCapturedCallsAndStoreAddresses()
    {
        var input = SpirvSynchronizationTests.RawOrderedFixture(); var original = Read(input); var graph = original.Copy();
        string before = ControlFlowPrinter.Write(original);
        var originalCalls = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Call).ToArray();
        var originalStores = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Store).ToArray();
        var edges = graph.Blocks.SelectMany(b => b.Terminator!.Edges).ToArray();
        SpirvSynchronizationLowering.Run(graph,input);
        var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
        Assert.Equal(originalCalls,instructions.Where(i => i.Operation is ValueOperation.Call));
        Assert.Equal(originalStores,instructions.Where(i => i.Operation is ValueOperation.Store));
        Assert.Equal(10,instructions.Count(i => i.Operation is ValueOperation.Barrier));
        Assert.Equal(edges,graph.Blocks.SelectMany(b => b.Terminator!.Edges));
        foreach (var block in graph.Blocks)
            for (int i=0;i<block.Instructions.Count;i++)
                if (block.Instructions[i].Operation is ValueOperation.Barrier) {
                    Assert.IsType<ValueOperation.Load>(block.Instructions[i+1].Operation);
                    Assert.IsType<ValueOperation.Barrier>(block.Instructions[i+2].Operation); i+=2;
                }
        Assert.Equal(before,ControlFlowPrinter.Write(original)); ControlFlowVerifier.Validate(graph,input);
    }

    [Fact]
    public void CompareExchangeDefaultAndSuppliedUnequalSemanticsRemainDistinct()
    {
        var input = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> counter:atomic<u32>; fn main(){_=atomicCompareExchangeWeak(&counter,1u,2u);}");
        input.VulkanMemoryModel = true; var graph = Read(input);
        var block = Assert.Single(graph.Blocks); int index=block.Instructions.FindIndex(i => i.Operation is ValueOperation.Builtin);
        SpirvSynchronizationLowering.Run(graph,input);
        var call = Assert.IsType<ValueOperation.Builtin>(block.Instructions[index].Operation);
        Assert.Equal(new SpirvAtomicMemory(5,0,0),call.AtomicMemory);
        var supplied = new SpirvAtomicMemory(1,72,66);
        block.Instructions[index] = block.Instructions[index] with { Operation = call with { AtomicMemory = supplied } };
        SpirvSynchronizationLowering.Run(graph,input);
        Assert.Same(supplied,Assert.IsType<ValueOperation.Builtin>(block.Instructions[index].Operation).AtomicMemory);
        ControlFlowVerifier.Validate(graph,input);
    }

    [Fact]
    public void UserFunctionsWithBuiltinNamesKeepTheirCallIdentity()
    {
        var input = new Module(); input.Functions.Add(new("workgroupUniformLoad")); input.Functions.Add(new("workgroupBarrier"));
        var caller = new ShaderFunction("main"); input.Functions.Add(caller);
        caller.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("workgroupUniformLoad",[],new ShaderType.Void())));
        caller.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("workgroupBarrier",[],new ShaderType.Void())));
        var graph = Read(input); var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
        SpirvSynchronizationLowering.Run(graph,input);
        Assert.Equal(instructions,graph.Blocks.SelectMany(b => b.Instructions));
        Assert.All(instructions,i => Assert.IsType<ValueOperation.Call>(i.Operation));
        ControlFlowVerifier.Validate(graph,input);
    }

    [Fact]
    public void LegacySynchronizationOnlyRewritesExplicitDeferrals()
    {
        var input = WgslReader.Parse("fn owned(){workgroupBarrier();} fn deferred(){workgroupBarrier();}");
        var output = SpirvSynchronizationLowering.Run(input,new HashSet<string>(StringComparer.Ordinal) { "deferred" });
        Assert.Same(input.Functions[0],output.Functions[0]); Assert.NotSame(input.Functions[1],output.Functions[1]);
        Assert.IsType<Statement.Barrier>(Assert.Single(output.Functions[1].Body.Statements));
        Assert.IsType<Statement.Evaluate>(Assert.Single(input.Functions[1].Body.Statements));
    }
}

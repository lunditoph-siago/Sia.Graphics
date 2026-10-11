using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalMemoryLegalizationTests
{
    private static ControlFlowFunction Read(Module module, string name = "main")
    {
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(f => f.Name == name), module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); LocalValuePromotion.Run(graph!);
        ControlFlowVerifier.Validate(graph!, module); return graph!;
    }
    private static ValueOperation[] Operations(ControlFlowFunction graph) => graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).ToArray();

    [Fact]
    public void CapturedAddressKeepsExactMemberQualificationsAcrossShadowing()
    {
        var input = WgslReader.Parse("struct Data{qualified:u32,plain:u32} @group(0) @binding(0) var<storage,read_write> buffer:Data; @compute @workgroup_size(1) fn main(){let p=&buffer.qualified; {let buffer=9u; *p=buffer; let read=*p;} let plain=buffer.plain;}");
        input.VulkanMemoryModel = true;
        var data = (ShaderType.Structure)input.Globals[0].Type;
        ((IList<StructMember>)data.Members)[0] = data.Members[0] with { MemoryDecorations = MemoryDecorations.Volatile };
        input.Globals[0] = input.Globals[0] with { MemoryDecorations = MemoryDecorations.Coherent };
        var original = Read(input); var owned = original.Copy();
        string before = ControlFlowPrinter.Write(original);
        var ids = owned.Blocks.SelectMany(b => b.Instructions).Select(i => i.Result).ToArray();
        SpirvMemoryAccessLowering.Run(owned, input);
        var operations = Operations(owned);
        Assert.Equal(new SpirvMemoryAccess(41, AvailableScope: 5), Assert.Single(operations.OfType<ValueOperation.Store>()).MemoryAccess);
        var loads = operations.OfType<ValueOperation.Load>().ToArray(); Assert.Equal(2, loads.Length);
        Assert.Equal(new SpirvMemoryAccess(49, VisibleScope: 5), loads[0].MemoryAccess);
        Assert.Equal(new SpirvMemoryAccess(48, VisibleScope: 5), loads[1].MemoryAccess);
        Assert.Equal(ids, owned.Blocks.SelectMany(b => b.Instructions).Select(i => i.Result));
        Assert.Equal(before, ControlFlowPrinter.Write(original));
        Assert.All(Operations(original).OfType<ValueOperation.Load>(), l => Assert.Null(l.MemoryAccess));
        ControlFlowVerifier.Validate(owned, input);
        string once = ControlFlowPrinter.Write(owned); SpirvMemoryAccessLowering.Run(owned, input);
        Assert.Equal(once, ControlFlowPrinter.Write(owned));
    }

    [Fact]
    public void LoopPointerParametersReachAQualificationFixedPointWithoutChangingEdges()
    {
        var input = new Module { VulkanMemoryModel = true };
        input.Globals.Add(new("a", ShaderType.U32, AddressSpace.Storage) { MemoryDecorations = MemoryDecorations.Coherent });
        input.Globals.Add(new("b", ShaderType.U32, AddressSpace.Storage) { MemoryDecorations = MemoryDecorations.Volatile });
        var signature = new ShaderFunction("main"); input.Functions.Add(signature);
        var graph = new ControlFlowFunction(signature);
        var entry = graph.Block(); var loop = graph.Block(); var exit = graph.Block(); graph.Entry = entry.Id;
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage);
        var a = graph.Value(pointer); var b = graph.Value(pointer); var condition = graph.Value(ShaderType.Bool);
        var phi = graph.Value(pointer); loop.Parameters.Add(phi);
        entry.Instructions.Add(new(a, new ValueOperation.Symbol("a")));
        entry.Instructions.Add(new(b, new ValueOperation.Symbol("b")));
        entry.Instructions.Add(new(condition, new ValueOperation.Literal(false)));
        entry.Terminator = new ControlFlowTerminator.Branch(new(loop.Id, [a]));
        loop.Instructions.Add(new(graph.Value(ShaderType.U32), new ValueOperation.Load(phi)));
        loop.Terminator = new ControlFlowTerminator.Conditional(condition, new(loop.Id, [b]), new(exit.Id));
        exit.Terminator = new ControlFlowTerminator.Return();
        ControlFlowVerifier.Validate(graph, input);
        var edges = graph.Blocks.SelectMany(block => block.Terminator!.Edges).ToArray();
        SpirvMemoryAccessLowering.Run(graph, input);
        Assert.Equal(new SpirvMemoryAccess(49, VisibleScope: 5), Assert.Single(Operations(graph).OfType<ValueOperation.Load>()).MemoryAccess);
        Assert.Equal(edges, graph.Blocks.SelectMany(block => block.Terminator!.Edges));
        Assert.Equal(phi, Assert.Single(loop.Parameters));
        ControlFlowVerifier.Validate(graph, input);
    }

    [Fact]
    public void FunctionSnapshotRetainsNativeOperandsWithoutAggregateQualification()
    {
        var type = new ShaderType.Structure("Data", [new StructMember("value", ShaderType.U32) { MemoryDecorations = MemoryDecorations.Coherent | MemoryDecorations.Volatile }]);
        var input = new Module { VulkanMemoryModel = true }; input.Structures.Add(type);
        var function = new ShaderFunction("main"); input.Functions.Add(function);
        function.Body.Statements.Add(new Statement.Declare("snapshot", type, new Expression.Construct(type, [Expression.U32(9)])));
        var member = new Expression.Member(new Expression.Reference("snapshot", type), "value", ShaderType.U32);
        function.Body.Statements.Add(new Statement.Declare("read", ShaderType.U32, new Expression.Load(member) { MemoryAccess = new(3, Alignment: 4) }, false));
        var graph = Read(input); SpirvMemoryAccessLowering.Run(graph, input);
        Assert.Equal(new SpirvMemoryAccess(3, Alignment: 4), Assert.Single(Operations(graph).OfType<ValueOperation.Load>()).MemoryAccess);
        Assert.All(Operations(graph).OfType<ValueOperation.Store>(), s => Assert.Null(s.MemoryAccess));
        ControlFlowVerifier.Validate(graph, input);
    }

    [Fact]
    public void StorageFormalUsesConservativeRequirementsDespiteGlobalNameShadowing()
    {
        var input = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> p:u32; @group(0) @binding(1) var<storage,read_write> other:u32; fn helper(p:ptr<storage,u32,read_write>)->u32{return *p;}");
        input.VulkanMemoryModel = true;
        input.Globals[1] = input.Globals[1] with { MemoryDecorations = MemoryDecorations.Coherent | MemoryDecorations.Volatile };
        var graph = Read(input, "helper"); SpirvMemoryAccessLowering.Run(graph, input);
        Assert.Equal(new SpirvMemoryAccess(49, VisibleScope: 5), Assert.Single(Operations(graph).OfType<ValueOperation.Load>()).MemoryAccess);
        ControlFlowVerifier.Validate(graph, input);
    }

    [Fact]
    public void TargetCallsReceiveQualifiedMemoryEffectsFromTheirGraphHelper()
    {
        var input = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> buffer:u32; fn helper(){buffer=7u;} @compute @workgroup_size(1) fn main(){helper();}");
        input.VulkanMemoryModel = true;
        input.Globals[0] = input.Globals[0] with { MemoryDecorations = MemoryDecorations.Coherent | MemoryDecorations.Volatile };
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        var call = Assert.Single(Operations(prepared.ControlFlow["main"].Graph).OfType<ValueOperation.Call>());
        Assert.Equal(ShaderEffects.MemoryOrdering | ShaderEffects.Volatile, call.CalleeEffects & (ShaderEffects.MemoryOrdering | ShaderEffects.Volatile));
        Assert.Null(Assert.Single(prepared.Module.Functions.Single(f => f.Name == "helper").Body.Statements.OfType<Statement.Store>()).MemoryAccess);
        Assert.Equal(new SpirvMemoryAccess(41, AvailableScope: 5), Assert.Single(Operations(prepared.ControlFlow["helper"].Graph).OfType<ValueOperation.Store>()).MemoryAccess);
    }

    [Fact]
    public void LegacyAdapterQualifiesOnlyExplicitlySelectedBodies()
    {
        var input = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> buffer:u32; fn owned(){buffer=1u;} fn deferred(){buffer=2u;}");
        input.VulkanMemoryModel = true;
        input.Globals[0] = input.Globals[0] with { MemoryDecorations = MemoryDecorations.Coherent };
        var output = SpirvMemoryAccessLowering.Run(input, new HashSet<string>(StringComparer.Ordinal) { "deferred" });
        Assert.Same(input.Functions[0], output.Functions[0]); Assert.NotSame(input.Functions[1], output.Functions[1]);
        Assert.Null(Assert.Single(input.Functions[1].Body.Statements.OfType<Statement.Store>()).MemoryAccess);
        Assert.Equal(new SpirvMemoryAccess(40, AvailableScope: 5), Assert.Single(output.Functions[1].Body.Statements.OfType<Statement.Store>()).MemoryAccess);
        Assert.Same(output, SpirvMemoryAccessLowering.Run(output, new HashSet<string>(StringComparer.Ordinal) { "deferred" }));
    }
}

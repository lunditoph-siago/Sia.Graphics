using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalWorkgroupAccessTests
{
    private static ControlFlowFunction Read(Module module, string name = "main")
    {
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(f => f.Name == name), module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); LocalValuePromotion.Run(graph!);
        ControlFlowVerifier.Validate(graph!, module); return graph!;
    }

    [Fact]
    public void SignatureCopyRetainsAllocatorHistoryAndBorrowsNeitherEdgesNorLists()
    {
        var original = new ControlFlowFunction(new("original"));
        var entry = original.Block(); var exit = original.Block(); original.Entry = entry.Id;
        var value = original.Value(ShaderType.U32); exit.Parameters.Add(value);
        entry.Terminator = new ControlFlowTerminator.Branch(new(exit.Id, [value])); exit.Terminator = new ControlFlowTerminator.Return();
        var reserved = original.Value(ShaderType.U32); var removed = original.Block(); original.Blocks.Remove(removed);
        var signature = new ShaderFunction("physical"); var copy = original.Copy(signature);
        Assert.Same(signature, copy.Signature); Assert.Equal("original", original.Signature.Name);
        Assert.Equal(reserved.Id + 1, copy.Value(ShaderType.U32).Id); Assert.Equal(removed.Id + 1, copy.Block().Id);
        var oldEdge = Assert.Single(entry.Terminator.Edges); var newEdge = Assert.Single(copy.Blocks[0].Terminator!.Edges);
        Assert.NotSame(oldEdge, newEdge); newEdge.Arguments.Clear(); Assert.Single(oldEdge.Arguments);
        copy.Blocks[1].Parameters.Clear(); Assert.Single(exit.Parameters);
    }

    internal static Module PointerSignatureFixture()
    {
        var module = WgslReader.Parse("struct Data{values:array<u32,2>,tail:u32,} var<workgroup> group_data:Data; @group(0) @binding(0) var<storage,read_write> output:Data; fn read(p:ptr<workgroup,Data>)->Data{return *p;} @compute @workgroup_size(1) fn main(){group_data=Data(array<u32,2>(11u,22u),33u);output=read(&group_data);}");
        var read = module.Functions.Single(f => f.Name == "read"); var pointer = read.Arguments[0].Type;
        var pass = new ShaderFunction("forward") { ReturnType = pointer };
        pass.Arguments.Add(new("p", pointer)); pass.Body.Statements.Add(new Statement.Return(new Expression.Reference("p", pointer)));
        pass.DiagnosticFilters.Add(new(DiagnosticSeverity.Warning, "derivative_uniformity"));
        module.Functions.Insert(0, pass);
        read.Body = new(); read.Body.Statements.Add(new Statement.Declare("chosen", pointer, new Expression.Call("forward",
            [new Expression.Reference("p", pointer)], pointer, CallBinding.Function), false));
        read.Body.Statements.Add(new Statement.Return(new Expression.Load(new Expression.Unary("*", new Expression.Reference("chosen", pointer), pointer))));
        ModuleValidator.ValidateNative(module); return module;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PointerSignaturesCallsAndReturnedSsaAddressesMapTogether(bool vulkan)
    {
        var input = PointerSignatureFixture(); input.VulkanMemoryModel = vulkan;
        var original = Read(input, "forward"); string before = ControlFlowPrinter.Write(original);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        ModuleValidator.Validate(prepared.Canonical, native: true);
        var physical = prepared.WorkgroupTypes[input.Structures.Single()];
        var forward = prepared.Module.Functions.Single(f => f.Name == "forward");
        Assert.Equal(physical, Assert.IsType<ShaderType.Pointer>(forward.ReturnType).Base);
        Assert.Equal(forward.ReturnType, Assert.Single(forward.Arguments).Type);
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(forward.DiagnosticFilters).Severity);
        var graph = prepared.ControlFlow["forward"].Graph; Assert.Same(forward, graph.Signature);
        Assert.Equal(forward.ReturnType, Assert.IsType<ControlFlowTerminator.Return>(Assert.Single(graph.Blocks).Terminator).Value!.Value.Type);
        var call = Assert.IsType<ValueOperation.Call>(Assert.Single(prepared.ControlFlow["read"].Graph.Blocks.SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Call { Function: "forward" }).Operation);
        Assert.Equal(forward.ReturnType, call.ReturnType); Assert.Equal(forward.ReturnType, Assert.Single(call.Arguments).Type);
        Assert.Equal(before, ControlFlowPrinter.Write(original));
        Assert.Equal(input.Structures.Single(), Assert.IsType<ShaderType.Pointer>(input.Functions.Single(f => f.Name == "forward").ReturnType).Base);
        ModuleValidator.ValidateNative(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CapturedAccessesKeepNativeOperandsResultsAndDiagnosticOrigins(bool collective)
    {
        var input = WgslReader.Parse(collective ? SpirvWorkgroupAccessTests.UniformSource : SpirvWorkgroupConversionTests.NestedSource);
        var original = Read(input); var graph = original.Copy();
        var origin = new SourceSpan(3, 7); var filter = new DiagnosticFilter(DiagnosticSeverity.Warning, "derivative_uniformity");
        var memory = new SpirvMemoryAccess(2, Alignment: 16);
        foreach (var block in graph.Blocks)
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                if (instruction.Operation is ValueOperation.Load { Pointer.Type: ShaderType.Pointer { Space: AddressSpace.Workgroup } } load)
                    block.Instructions[i] = instruction with { Span = origin, DiagnosticFilters = [filter], Operation = load with { MemoryAccess = memory } };
                if (instruction.Operation is ValueOperation.Builtin { Function: "workgroupUniformLoad" } builtin)
                    block.Instructions[i] = instruction with { Span = origin, DiagnosticFilters = [filter], Operation = builtin with { MemoryAccess = memory } };
            }
        var before = ControlFlowPrinter.Write(original); var edges = graph.Blocks.SelectMany(b => b.Terminator!.Edges).ToArray();
        var read = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Span == origin);
        var address = Assert.Single(read.Operation.Operands); var result = read.Result;
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        SpirvWorkgroupAccessLowering.Run(graph, prepared);
        ControlFlowVerifier.Validate(graph, prepared.Module);
        var physicalRead = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Load { MemoryAccess: not null }
            || i.Operation is ValueOperation.Builtin { Function: "workgroupUniformLoad" });
        Assert.Equal(address.Id, Assert.Single(physicalRead.Operation.Operands).Id);
        Assert.Equal(memory, physicalRead.Operation is ValueOperation.Load l ? l.MemoryAccess : ((ValueOperation.Builtin)physicalRead.Operation).MemoryAccess);
        var conversion = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Result == result);
        Assert.IsType<ValueOperation.Call>(conversion.Operation);
        Assert.Equal(origin, conversion.Span); Assert.Equal(filter, Assert.Single(conversion.DiagnosticFilters));
        Assert.Equal(origin, physicalRead.Span); Assert.Equal(filter, Assert.Single(physicalRead.DiagnosticFilters));
        Assert.Equal(edges, graph.Blocks.SelectMany(b => b.Terminator!.Edges)); Assert.Equal(before, ControlFlowPrinter.Write(original));
    }

    [Fact]
    public void LoopCarriedPointerArgumentsKeepTopologyAndPhysicalTypes()
    {
        var input = WgslReader.Parse(SpirvWorkgroupConversionTests.NestedSource);
        var main = input.Functions.Single(f => f.Name == "main");
        var logical = input.Globals.Single(g => g.Name == "group_data").Type;
        var pointer = new ShaderType.Pointer(logical, AddressSpace.Workgroup);
        var graph = new ControlFlowFunction(main); var entry = graph.Block(); var header = graph.Block(); var body = graph.Block(); var exit = graph.Block();
        graph.Entry = entry.Id; var address = graph.Value(pointer); var carried = graph.Value(pointer); var condition = graph.Value(ShaderType.Bool);
        entry.Instructions.Add(new(address, new ValueOperation.Symbol("group_data")));
        entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [address])); header.Parameters.Add(carried);
        header.Instructions.Add(new(condition, new ValueOperation.Literal(false)));
        header.Terminator = new ControlFlowTerminator.Conditional(condition, new(body.Id), new(exit.Id));
        body.Instructions.Add(new(graph.Value(logical), new ValueOperation.Load(carried)));
        body.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [carried])); exit.Terminator = new ControlFlowTerminator.Return();
        graph.Loops.Add(header.Id, new(body.Id, exit.Id)); ControlFlowVerifier.Validate(graph, input);
        var original = graph.Copy(); string before = ControlFlowPrinter.Write(original);
        var edges = graph.Blocks.SelectMany(b => b.Terminator!.Edges).ToArray();
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input); SpirvWorkgroupAccessLowering.Run(graph, prepared);
        ControlFlowVerifier.Validate(graph, prepared.Module);
        Assert.Equal(prepared.WorkgroupTypes[logical], Assert.IsType<ShaderType.Pointer>(Assert.Single(header.Parameters).Type).Base);
        Assert.All(graph.Blocks.SelectMany(b => b.Terminator!.Edges).SelectMany(e => e.Arguments),
            v => Assert.Equal(prepared.WorkgroupTypes[logical], Assert.IsType<ShaderType.Pointer>(v.Type).Base));
        Assert.Equal(edges, graph.Blocks.SelectMany(b => b.Terminator!.Edges));
        Assert.Equal(original.Loops, graph.Loops); Assert.Equal(before, ControlFlowPrinter.Write(original));
    }

    [Fact]
    public void RepreparationRetainsGraphsAndTargetLabelsWithoutReadingBorrowedBodies()
    {
        var input = WgslReader.Parse(SpirvWorkgroupAccessTests.AliasSource);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input); var main = prepared.Module.Functions.Single(f => f.Name == "main");
        string before = ControlFlowPrinter.Write(prepared.ControlFlow["main"].Graph);
        byte[] binary = SpirvWriter.Emit(prepared).ToBytes(); var body = main.Body;
        try {
            main.Body = new(); main.Body.Statements.Add(new Statement.Return(Expression.U32(999)));
            var again = SpirvControlFlowLowering.Prepare(prepared);
            ModuleValidator.Validate(again.Canonical);
            Assert.Equal(before, ControlFlowPrinter.Write(again.ControlFlow["main"].Graph));
            Assert.Equal(prepared.ControlFlow["main"].BlockOrder, again.ControlFlow["main"].BlockOrder);
            Assert.Equal(binary, SpirvWriter.Emit(again).ToBytes());
        }
        finally { main.Body = body; }
    }
}

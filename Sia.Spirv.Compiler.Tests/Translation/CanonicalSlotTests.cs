using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalSlotTests
{
    private static readonly ShaderType.Pointer Address = new(ShaderType.U32, AddressSpace.Function);
    private static readonly ShaderType.Pointer Slot = new(Address, AddressSpace.Function);
    private static SsaValue Add(ControlFlowFunction graph, ControlFlowBlock block, ShaderType type, ValueOperation operation)
    {
        var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value;
    }
    private static Module Context() => WgslReader.Parse("@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;@compute @workgroup_size(1) fn main(){}");
    private static void Store(ControlFlowBlock block, SsaValue place, SsaValue value, SpirvMemoryAccess? access = null)
        => block.Instructions.Add(new(null, new ValueOperation.Store(place, value, access)));
    private static void Output(Module module, ControlFlowFunction graph, ControlFlowBlock block, uint index, SsaValue value)
    {
        var output = module.Globals.Single(g => g.Name == "outputs");
        var root = Add(graph, block, new ShaderType.Pointer(output.Type, output.Space), new ValueOperation.Symbol(output.Name));
        var element = Add(graph, block, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage),
            new ValueOperation.Access(root, Add(graph, block, ShaderType.U32, new ValueOperation.Literal(index))));
        Store(block, element, value);
    }
    private static Module Lower(Module module, ControlFlowFunction graph)
    {
        ControlFlowVerifier.Validate(graph, module);
        LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Local
            && i.Result?.Type is ShaderType.Pointer { Base: ShaderType.Pointer });
        var signature = StructuredControlFlowLowering.Run(graph, module);
        module.Functions[module.Functions.FindIndex(f => f.Name == signature.Name)] = signature;
        module = new PointerSelectionLowering(module).Run(); ModuleValidator.Validate(module);
        return module;
    }
    private static void Check(Module module, uint input, uint[] expected)
    {
        var candidates = new[] { module, WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)) };
        foreach (var candidate in candidates) Assert.Equal(expected, new CanonicalExecution(candidate, [input]).Run().Output);
    }

    internal static Module Snapshot(bool reverse)
    {
        var module = Context(); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        var a = Add(graph, entry, Address, new ValueOperation.Local("a"));
        var b = Add(graph, entry, Address, new ValueOperation.Local("b"));
        Store(entry, a, Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(11u)));
        Store(entry, b, Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(23u)));
        var slot = Add(graph, entry, Slot, new ValueOperation.Local("slot", false));
        Store(entry, slot, reverse ? b : a);
        var captured = Add(graph, entry, Address, new ValueOperation.Load(slot));
        Store(entry, slot, reverse ? a : b);
        Store(entry, captured, Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(18u)));
        Output(module, graph, entry, 0, Add(graph, entry, ShaderType.U32, new ValueOperation.Load(a)));
        Output(module, graph, entry, 1, Add(graph, entry, ShaderType.U32, new ValueOperation.Load(b)));
        entry.Terminator = new ControlFlowTerminator.Return(); return Lower(module, graph);
    }
    [Theory]
    [InlineData(false, 18u, 23u)] [InlineData(true, 11u, 18u)]
    public void LoadedAddressRemainsCapturedAfterSlotOverwrite(bool reverse, uint first, uint second)
        => Check(Snapshot(reverse), 0, [first, second]);

    internal static Module Branch()
    {
        var module = Context(); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        var left = graph.Block(); var right = graph.Block(); var merge = graph.Block();
        var a = Add(graph, entry, Address, new ValueOperation.Local("a"));
        var b = Add(graph, entry, Address, new ValueOperation.Local("b"));
        Store(entry, a, Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(11u)));
        Store(entry, b, Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(23u)));
        var slot = Add(graph, entry, Slot, new ValueOperation.Local("slot", false));
        var inputs = module.Globals.Single(g => g.Name == "inputs");
        var root = Add(graph, entry, new ShaderType.Pointer(inputs.Type, inputs.Space, inputs.Access), new ValueOperation.Symbol(inputs.Name));
        var index = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(0u));
        var address = Add(graph, entry, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read), new ValueOperation.Access(root, index));
        var condition = Add(graph, entry, ShaderType.Bool, new ValueOperation.Binary("==", Add(graph, entry, ShaderType.U32, new ValueOperation.Load(address)), index));
        entry.Terminator = new ControlFlowTerminator.Conditional(condition, new(left.Id), new(right.Id));
        Store(left, slot, a); left.Terminator = new ControlFlowTerminator.Branch(new(merge.Id));
        Store(right, slot, b); right.Terminator = new ControlFlowTerminator.Branch(new(merge.Id));
        var chosen = Add(graph, merge, Address, new ValueOperation.Load(slot));
        Store(merge, chosen, Add(graph, merge, ShaderType.U32, new ValueOperation.Literal(18u)));
        Output(module, graph, merge, 0, Add(graph, merge, ShaderType.U32, new ValueOperation.Load(a)));
        Output(module, graph, merge, 1, Add(graph, merge, ShaderType.U32, new ValueOperation.Load(b)));
        merge.Terminator = new ControlFlowTerminator.Return(); graph.SelectionMerges.Add(entry.Id, merge.Id);
        var result = Lower(module, graph);
        Assert.Contains(graph.Blocks, block => block.Parameters.Any(p => p.Type == Address));
        return result;
    }
    [Theory]
    [InlineData(0u, 18u, 23u)] [InlineData(5u, 11u, 18u)]
    public void AllBranchAssignmentsFormAnAddressMergeWithoutImplicitZero(uint input, uint first, uint second)
        => Check(Branch(), input, [first, second]);

    [Theory]
    [InlineData("uninitialized")] [InlineData("partial-branch")] [InlineData("loop-only")]
    [InlineData("escaped")] [InlineData("qualified")]
    public void UnsafeOrObservableSlotMemoryRemainsExplicit(string kind)
    {
        var module = Context(); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        var a = Add(graph, entry, Address, new ValueOperation.Local("a"));
        var slot = Add(graph, entry, Slot, new ValueOperation.Local("slot", false));
        var end = entry;
        if (kind is "partial-branch" or "loop-only") {
            var yes = graph.Block(); var no = graph.Block(); end = graph.Block();
            var header = kind == "loop-only" ? graph.Block() : entry;
            if (header != entry) entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id));
            var condition = Add(graph, header, ShaderType.Bool, new ValueOperation.Literal(true));
            header.Terminator = new ControlFlowTerminator.Conditional(condition, new(yes.Id), new(no.Id));
            Store(yes, slot, a);
            yes.Terminator = new ControlFlowTerminator.Branch(new(kind == "loop-only" ? header.Id : end.Id));
            no.Terminator = new ControlFlowTerminator.Branch(new(end.Id));
            if (kind == "loop-only") graph.Loops.Add(header.Id, new(yes.Id, end.Id));
            else graph.SelectionMerges.Add(header.Id, end.Id);
        }
        else if (kind != "uninitialized") Store(entry, slot, a);
        if (kind == "escaped") _ = Add(graph, end, Slot, new ValueOperation.Let("alias", slot));
        var memory = kind == "qualified" ? new SpirvMemoryAccess(1) : null;
        _ = Add(graph, end, Address, new ValueOperation.Load(slot, memory));
        end.Terminator = new ControlFlowTerminator.Return();
        ControlFlowVerifier.Validate(graph, module);
        string before = ControlFlowPrinter.Write(graph);
        LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
        Assert.Contains(graph.Blocks.SelectMany(b => b.Instructions), i => i.Result == slot && i.Operation is ValueOperation.Local);
        Assert.Contains(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Load load && load.Pointer == slot && load.MemoryAccess == memory);
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Construct && i.Result?.Type is ShaderType.Pointer);
        Assert.Contains("local slot", before);
    }

    [Fact]
    public void DefinitelyAssignedAllocationOnlyDataAlsoPromotesWithoutAddingZero()
    {
        var module = Context(); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        var slot = Add(graph, entry, Address, new ValueOperation.Local("data", false));
        var seven = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(7u)); Store(entry, slot, seven);
        Output(module, graph, entry, 0, Add(graph, entry, ShaderType.U32, new ValueOperation.Load(slot)));
        entry.Terminator = new ControlFlowTerminator.Return(); ControlFlowVerifier.Validate(graph, module);
        LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Local or ValueOperation.Construct);
        module.Functions[0] = StructuredControlFlowLowering.Run(graph, module);
        Assert.Equal(new uint[] { 7, 0 }, new CanonicalExecution(module, [0]).Run().Output);
    }

    internal static Module Loop()
    {
        var module = Context(); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        var header = graph.Block(); var body = graph.Block(); var continuing = graph.Block(); var end = graph.Block();
        var a = Add(graph, entry, Address, new ValueOperation.Local("a"));
        var b = Add(graph, entry, Address, new ValueOperation.Local("b"));
        Store(entry, a, Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(11u)));
        Store(entry, b, Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(23u)));
        var left = Add(graph, entry, Slot, new ValueOperation.Local("left", false));
        var right = Add(graph, entry, Slot, new ValueOperation.Local("right", false));
        Store(entry, left, a); Store(entry, right, b);
        var counter = Add(graph, entry, Address, new ValueOperation.Local("counter"));
        var zero = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(0u));
        var one = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(1u));
        var two = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(2u));
        var inputs = module.Globals.Single(g => g.Name == "inputs");
        var root = Add(graph, entry, new ShaderType.Pointer(inputs.Type, inputs.Space, inputs.Access), new ValueOperation.Symbol(inputs.Name));
        var address = Add(graph, entry, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read), new ValueOperation.Access(root, zero));
        var limit = Add(graph, entry, ShaderType.U32, new ValueOperation.Load(address));
        entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id));
        var iteration = Add(graph, header, ShaderType.U32, new ValueOperation.Load(counter));
        var condition = Add(graph, header, ShaderType.Bool, new ValueOperation.Binary("<", iteration, limit));
        header.Terminator = new ControlFlowTerminator.Conditional(condition, new(body.Id), new(end.Id));
        var oldLeft = Add(graph, body, Address, new ValueOperation.Load(left));
        var oldRight = Add(graph, body, Address, new ValueOperation.Load(right));
        Store(body, left, oldRight); Store(body, right, oldLeft);
        Store(body, oldLeft, Add(graph, body, ShaderType.U32, new ValueOperation.Binary("+", Add(graph, body, ShaderType.U32, new ValueOperation.Load(oldLeft)), one)));
        Store(body, oldRight, Add(graph, body, ShaderType.U32, new ValueOperation.Binary("+", Add(graph, body, ShaderType.U32, new ValueOperation.Load(oldRight)), two)));
        body.Terminator = new ControlFlowTerminator.Branch(new(continuing.Id));
        Store(continuing, counter, Add(graph, continuing, ShaderType.U32, new ValueOperation.Binary("+", iteration, one)));
        continuing.Terminator = new ControlFlowTerminator.Branch(new(header.Id));
        Output(module, graph, end, 0, Add(graph, end, ShaderType.U32, new ValueOperation.Load(Add(graph, end, Address, new ValueOperation.Load(left)))));
        Output(module, graph, end, 1, Add(graph, end, ShaderType.U32, new ValueOperation.Load(Add(graph, end, Address, new ValueOperation.Load(right)))));
        end.Terminator = new ControlFlowTerminator.Return(); graph.Loops.Add(header.Id, new(continuing.Id, end.Id));
        return Lower(module, graph);
    }
    [Theory]
    [InlineData(0u, 11u, 23u)] [InlineData(1u, 25u, 12u)] [InlineData(2u, 14u, 26u)]
    [InlineData(3u, 28u, 15u)] [InlineData(5u, 31u, 18u)]
    public void LoopSlotSwapsKeepParallelAddressSnapshots(uint input, uint first, uint second)
        => Check(Loop(), input, [first, second]);

    [Fact]
    public void PointerAllocationCannotRequestAnInventedZeroAddress()
    {
        var module = Context(); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id;
        _ = Add(graph, entry, Slot, new ValueOperation.Local("slot"));
        entry.Terminator = new ControlFlowTerminator.Return();
        Assert.Contains("illegal Local", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
    }
}

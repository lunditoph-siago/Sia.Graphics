using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalReferenceMemoryTests
{
    internal static SpirvBinary QualifiedFunctionFixture() => PointerMemoryTests.StorePointers(
        NativeCanonicalPointerPhiTests.Fixture(), "native", qualified: true);

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void SlotLoweringUsesOwnedGraphsAndPreservesMemoryMetadata(bool privateSlot, bool volatileSlot)
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;@compute @workgroup_size(1) fn main(){}");
        var graph = new ControlFlowFunction(module.Functions.Single()); var block = graph.Block(); graph.Entry = block.Id;
        SsaValue Add(ShaderType type, ValueOperation operation) { var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value; }
        SsaValue Root(GlobalVariable global) => Add(new ShaderType.Pointer(global.Type, global.Space, global.Access), new ValueOperation.Symbol(global.Name));
        var input = Root(module.Globals[0]); var output = Root(module.Globals[1]);
        var zero = Add(ShaderType.U32, new ValueOperation.Literal(0u)); var one = Add(ShaderType.U32, new ValueOperation.Literal(1u));
        var index = Add(ShaderType.U32, new ValueOperation.Load(Add(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read), new ValueOperation.Access(input, zero))));
        var opposite = Add(ShaderType.U32, new ValueOperation.Binary("-", one, index));
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage);
        var before = Add(pointer, new ValueOperation.Access(output, index)); var after = Add(pointer, new ValueOperation.Access(output, opposite));
        var slotType = new ShaderType.Pointer(pointer, privateSlot ? AddressSpace.Private : AddressSpace.Function);
        if (privateSlot) module.Globals.Add(new("slot", pointer, AddressSpace.Private));
        var slot = Add(slotType, privateSlot ? new ValueOperation.Symbol("slot") : new ValueOperation.Local("slot", false));
        var alias = Add(slotType, new ValueOperation.Let("alias", slot));
        var span = new SourceSpan(40, 8); DiagnosticFilter[] filters = [new(DiagnosticSeverity.Off, "derivative_uniformity")];
        var memory = new SpirvMemoryAccess(volatileSlot ? 7u : 6u, Alignment: 4);
        void Store(SsaValue value) => block.Instructions.Add(new(null, new ValueOperation.Store(alias, value, memory), span) { DiagnosticFilters = filters });
        SsaValue Load() { var value = graph.Value(pointer); block.Instructions.Add(new(value, new ValueOperation.Load(alias, memory), span) { DiagnosticFilters = filters }); return value; }
        Store(before); var snapshot = Load(); Store(after); var current = Load();
        block.Instructions.Add(new(null, new ValueOperation.Store(snapshot, Add(ShaderType.U32, new ValueOperation.Literal(19u)))));
        block.Instructions.Add(new(null, new ValueOperation.Store(current, Add(ShaderType.U32, new ValueOperation.Literal(37u)))));
        block.Terminator = new ControlFlowTerminator.Return();
        var source = new CanonicalModule(module, new Dictionary<string, ControlFlowFunction> { [graph.Signature.Name] = graph },
            new Dictionary<string, string>(), new HashSet<string> { graph.Signature.Name });
        ModuleValidator.Validate(source, native: true); string original = ControlFlowPrinter.Write(graph);
        module.Functions.Single().Body = new Block { Statements = { new Statement.Evaluate(new Expression.Reference("obsolete", ShaderType.U32)) } };
        var target = CanonicalReferenceLowering.Run(source);
        Assert.Equal(original, ControlFlowPrinter.Write(graph)); ModuleValidator.Validate(target, native: true);
        var accesses = target.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions).Where(i => i.Span == span).ToArray();
        Assert.Equal(8, accesses.Length);
        Assert.All(accesses, i => {
            Assert.Equal(filters, i.DiagnosticFilters);
            Assert.Equal(memory, i.Operation switch { ValueOperation.Load load => load.MemoryAccess, ValueOperation.Store store => store.MemoryAccess, _ => null });
        });
        var structured = StructuredControlFlowLowering.Run(target);
        var native = SpirvReader.Parse(SpirvWriter.Write(structured, SpirvCompilationTarget.Default));
        foreach (uint sample in new uint[] { 0, 1 }) {
            uint[] expected = sample == 0 ? [19, 37] : [37, 19];
            Assert.Equal(expected, new CanonicalExecution(structured, [sample]).Run().Output);
            Assert.Equal(expected, new CanonicalExecution(native, [sample]).Run().Output);
            if (!volatileSlot) Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(structured, SpirvCompilationTarget.Default)), [sample]).Run().Output);
        }
        if (volatileSlot) Assert.Contains("volatile memory access", Assert.Throws<ShaderException>(() => WgslWriter.Write(structured, SpirvCompilationTarget.Default)).Message);
    }

    internal static SpirvBinary PrivateHelpers(bool volatileSlot)
    {
        var binary = PointerMemoryTests.StorePointers(NativeCanonicalPointerReturnTests.Fixture(nested: true, select: true, repeat: true),
            "select", qualified: true, privateSlot: true, slotVolatile: volatileSlot);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).ToDictionary(i => i.Operands[0], i => i.Operands[2]);
        var slots = code.Where(i => (Op)i.Opcode == Op.Variable && i.Operands[2] == 6 && pointers.ContainsKey(pointers[i.Operands[0]]))
            .ToDictionary(i => i.Operands[1], i => pointers[i.Operands[0]]);
        var aliases = code.Where(i => (Op)i.Opcode == Op.CopyObject && slots.ContainsKey(i.Operands[2]))
            .ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        uint Root(uint id) => aliases.GetValueOrDefault(id, id);
        uint voidType = code.Single(i => (Op)i.Opcode == Op.TypeVoid).Operands[0];
        var types = new List<SpirvInstruction>(); var helpers = new List<SpirvInstruction>(); var output = new List<SpirvInstruction>();
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        foreach (var instruction in code) {
            var a = instruction.Operands; var op = (Op)instruction.Opcode;
            bool store = op == Op.Store && slots.ContainsKey(Root(a[0]));
            bool load = op == Op.Load && slots.ContainsKey(Root(a[2]));
            if (!store && !load) { output.Add(instruction); continue; }
            uint slot = Root(a[store ? 0 : 2]), pointer = slots[slot];
            uint[] signatureTypes = store ? [voidType, pointer] : [pointer];
            var existing = code.Concat(types).FirstOrDefault(i => (Op)i.Opcode == Op.TypeFunction && i.Operands.Skip(1).SequenceEqual(signatureTypes));
            uint signature = existing?.Operands[0] ?? next++, function = next++, label = next++, value = next++;
            if (existing is null) types.Add(I(Op.TypeFunction, [signature, .. signatureTypes]));
            helpers.Add(I(Op.Function, store ? voidType : pointer, function, 0, signature));
            if (store) helpers.Add(I(Op.FunctionParameter, pointer, value));
            helpers.Add(I(Op.Label, label));
            helpers.Add(store ? I(Op.Store, [slot, value, .. a[2..]]) : I(Op.Load, [pointer, value, slot, .. a[3..]]));
            helpers.Add(store ? I(Op.Return) : I(Op.ReturnValue, value)); helpers.Add(I(Op.FunctionEnd));
            output.Add(store ? I(Op.FunctionCall, voidType, next++, function, a[1]) : I(Op.FunctionCall, pointer, a[1], function));
        }
        output.InsertRange(output.FindIndex(i => (Op)i.Opcode == Op.Function), types.Concat(helpers));
        return new() { Version = binary.Version, Bound = next, Instructions = output };
    }

    [Theory]
    [InlineData(false, 0u, 3u, 48u)] [InlineData(false, 5u, 22u, 42u)]
    [InlineData(true, 0u, 3u, 48u)] [InlineData(true, 5u, 22u, 42u)]
    public void PrivateSlotHelpersShareInvocationStorageAndRetainLoadedSnapshots(bool volatileSlot, uint input, uint first, uint second)
    {
        var binary = PrivateHelpers(volatileSlot); var original = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-slot-helper-expansion");
        Assert.Contains(traces, t => t.Pass == "native-cfg-import"); Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.Equal(original, binary.ToBytes()); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        var output = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(output.ToBytes()), [input]).Run().Output);
        if (volatileSlot) Assert.Contains("volatile memory access", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
        else Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Theory]
    [InlineData(false, false, 0u, 2u, 34u)] [InlineData(false, false, 5u, 14u, 26u)]
    [InlineData(false, true, 0u, 2u, 34u)] [InlineData(false, true, 5u, 14u, 26u)]
    [InlineData(true, false, 0u, 2u, 34u)] [InlineData(true, false, 5u, 14u, 26u)]
    [InlineData(true, true, 0u, 2u, 34u)] [InlineData(true, true, 5u, 14u, 26u)]
    public void QualifiedAndPrivateSlotsUseOwnedGraphs(bool privateSlot, bool volatileSlot, uint input, uint first, uint second)
    {
        var binary = PointerMemoryTests.StorePointers(NativeCanonicalPointerPhiTests.Fixture(), "native",
            qualified: true, privateSlot: privateSlot, slotVolatile: volatileSlot);
        byte[] original = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.Equal(original, binary.ToBytes());
        uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        var emitted = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        uint flags = volatileSlot ? 7u : 6u;
        Assert.Contains(emitted.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length == 5 && i.Operands[3] == flags && i.Operands[4] == 4);
        Assert.Contains(emitted.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length == 4 && i.Operands[2] == flags && i.Operands[3] == 4);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(emitted.ToBytes()), [input]).Run().Output);
        if (volatileSlot) Assert.Contains("volatile memory access", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
        else Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }
}

using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class PointerSlotTransferTests
{
    internal static SpirvBinary Fixture(string kind, string mode, string transfer, bool privateSlot, bool qualified, uint iterations = 3, uint version = 0, bool dualMasks = false)
    {
        var binary = PointerMemoryTests.Fixture(kind, mode, qualified, privateSlot, iterations, false, dualMasks && version == 0 ? 0x10400u : version);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).ToDictionary(i => i.Operands[0], i => i.Operands);
        var slots = code.Where(i => (Op)i.Opcode == Op.Variable && pointers.ContainsKey(pointers[i.Operands[0]][2]))
            .ToDictionary(i => i.Operands[1], i => i.Operands[0]);
        uint voidType = code.First(i => (Op)i.Opcode == Op.TypeVoid).Operands[0];
        var addedTypes = new List<SpirvInstruction>(); var variables = new List<SpirvInstruction>(); var helpers = new List<SpirvInstruction>();
        var helperIds = new Dictionary<(uint, uint, bool), uint>();
        var editHelpers = new Dictionary<uint, uint>();
        uint Root(uint id) => code.FirstOrDefault(i => (Op)i.Opcode == Op.CopyObject && i.Operands[1] == id) is { } copy ? Root(copy.Operands[2]) : id;
        uint SlotType(uint original)
        {
            if (transfer != "cross") return original;
            uint space = pointers[original][1] == 6 ? 7u : 6u, valueType = pointers[original][2];
            var old = pointers.Values.FirstOrDefault(a => a[1] == space && a[2] == valueType);
            if (old is not null) return old[0];
            uint id = next++; uint[] a = [id, space, valueType]; pointers.Add(id, a); addedTypes.Add(I(Op.TypePointer, a)); return id;
        }
        uint Helper(uint targetType, uint sourceType, bool store)
        {
            if (helperIds.TryGetValue((targetType, sourceType, store), out uint existing)) return existing;
            uint signature = next++, function = next++, target = next++, source = next++, label = next++;
            addedTypes.Add(I(Op.TypeFunction, signature, voidType, targetType, sourceType));
            helpers.AddRange([I(Op.Function, voidType, function, 0, signature), I(Op.FunctionParameter, targetType, target),
                I(Op.FunctionParameter, sourceType, source), I(Op.Label, label),
                store ? I(Op.Store, target, source, 6, 4) : I(Op.CopyMemory, target, source, 6, 4), I(Op.Return), I(Op.FunctionEnd)]);
            if (transfer == "nested-store")
            {
                uint wrapper = next++, p = next++, q = next++, entry = next++, call = next++;
                helpers.AddRange([I(Op.Function, voidType, wrapper, 0, signature), I(Op.FunctionParameter, targetType, p),
                    I(Op.FunctionParameter, sourceType, q), I(Op.Label, entry), I(Op.FunctionCall, voidType, call, function, p, q), I(Op.Return), I(Op.FunctionEnd)]);
                function = wrapper;
            }
            helperIds.Add((targetType, sourceType, store), function); return function;
        }
        uint EditHelper(uint slotType)
        {
            if (editHelpers.TryGetValue(slotType, out uint existing)) return existing;
            uint signature = next++, function = next++, p = next++, q = next++, label = next++, first = next++, second = next++, old = next++, value = next++, seven = next++;
            uint pointer = pointers[slotType][2], scalar = pointers[pointer][2];
            addedTypes.Add(I(Op.Constant, scalar, seven, 7)); addedTypes.Add(I(Op.TypeFunction, signature, voidType, slotType, slotType));
            helpers.AddRange([I(Op.Function, voidType, function, 0, signature), I(Op.FunctionParameter, slotType, p),
                I(Op.FunctionParameter, slotType, q), I(Op.Label, label), I(Op.Load, pointer, first, p, 6, 4), I(Op.Load, pointer, second, q, 6, 4),
                I(Op.Load, scalar, old, first), I(Op.IAdd, scalar, value, old, seven), I(Op.Store, second, value), I(Op.Return), I(Op.FunctionEnd)]);
            editHelpers.Add(slotType, function); return function;
        }
        var destinations = new Dictionary<uint, uint>(); var output = new List<SpirvInstruction>();
        foreach (var i in code)
        {
            var a = i.Operands;
            if (transfer is "helper-store" or "nested-store" && (Op)i.Opcode == Op.Store && slots.TryGetValue(Root(a[0]), out uint storeType))
            {
                uint function = Helper(storeType, pointers[storeType][2], true);
                output.Add(I(Op.FunctionCall, voidType, next++, function, Root(a[0]), a[1])); continue;
            }
            if (transfer is not ("helper-store" or "nested-store") && (Op)i.Opcode == Op.Load && pointers.ContainsKey(a[0]) && slots.TryGetValue(Root(a[2]), out uint sourceType))
            {
                uint root = Root(a[2]), targetType = SlotType(sourceType);
                if (transfer == "helper-edit")
                {
                    output.Add(I(Op.FunctionCall, voidType, next++, EditHelper(sourceType), root, root)); output.Add(i); continue;
                }
                if (!destinations.TryGetValue(root, out uint destination))
                {
                    destination = next++; destinations.Add(root, destination);
                    variables.Add(I(Op.Variable, targetType, destination, pointers[targetType][1]));
                }
                if (transfer == "helper-copy") output.Add(I(Op.FunctionCall, voidType, next++, Helper(targetType, sourceType, false), destination, root));
                else output.Add(I(Op.CopyMemory, destination, root, 6, 4));
                if (transfer == "cycle") output.AddRange([I(Op.CopyMemory, root, destination, 6, 4), I(Op.CopyMemory, destination, destination, 6, 4)]);
                output.Add(i with { Operands = [a[0], a[1], destination, .. a[3..]] }); continue;
            }
            output.Add(i);
        }
        int firstLabel = output.FindIndex(i => (Op)i.Opcode == Op.Label);
        output.InsertRange(firstLabel + 1, variables.Where(i => i.Operands[2] == 7));
        int firstFunction = output.FindIndex(i => (Op)i.Opcode == Op.Function);
        output.InsertRange(firstFunction, addedTypes.Concat(variables.Where(i => i.Operands[2] == 6)).Concat(helpers));
        if (dualMasks) output = output.Select(i => (Op)i.Opcode == Op.CopyMemory ? i with { Operands = [i.Operands[0], i.Operands[1], 6, 4, 2, 4] } : i).ToList();
        if (binary.Version >= 0x10400)
        {
            uint[] globals = variables.Where(i => i.Operands[2] == 6).Select(i => i.Operands[1]).ToArray();
            output = output.Select(i => (Op)i.Opcode == Op.EntryPoint ? i with { Operands = [.. i.Operands, .. globals] } : i).ToList();
        }
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = next, Instructions = output };
    }

    [Theory]
    [InlineData("scalar", "select", "copy", false, false)] [InlineData("array", "local", "copy", true, true)]
    [InlineData("same", "swap", "cycle", false, true)] [InlineData("mixed-struct", "local", "cross", false, true)]
    [InlineData("scalar", "select", "helper-copy", false, false)] [InlineData("array", "local", "helper-copy", true, true)]
    [InlineData("scalar", "select", "helper-store", false, false)] [InlineData("atomic", "swap", "helper-store", true, true)]
    [InlineData("scalar", "hybrid", "nested-store", false, true)] [InlineData("workgroup", "swap", "nested-store", true, false)]
    [InlineData("scalar", "select", "helper-edit", false, false)] [InlineData("scalar", "local", "helper-edit", true, true)]
    [InlineData("atomic", "select", "helper-edit", true, true)]
    public void SlotTransfersRetainAddressIdentityAndIndexSnapshots(string kind, string mode, string transfer, bool privateSlot, bool qualified)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, transfer, privateSlot, qualified).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void SlotCopiesRetainIndependentNativeSourceAndDestinationMasks(bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture("scalar", "local", "copy", privateSlot, true, 1, 0x10400, true).ToBytes());
        ModuleValidator.Validate(module);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length == 5 && i.Operands[3] == 2);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length == 4 && i.Operands[2] == 6);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes())); ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    internal static SpirvBinary StoreOnlyFixture(bool privateSlot)
    {
        var binary = MatrixHelperTests.PointerProducerFixture("load");
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Load).ToList();
        var variable = code.Single(i => (Op)i.Opcode == Op.Variable && i.Operands[1] == 33);
        int store = code.FindIndex(i => (Op)i.Opcode == Op.Store);
        code.Insert(store + 1, new((ushort)Op.CopyMemory, [33, 33]));
        if (privateSlot)
        {
            int type = code.FindIndex(i => (Op)i.Opcode == Op.TypePointer && i.Operands[0] == 12);
            code[type] = code[type] with { Operands = [12, 6, 6] };
            code.Remove(variable); code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), variable with { Operands = [12, 33, 6] });
        }
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = code };
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void WriteOnlyAndSelfCopiedSlotsDoNotRequireALoadToNormalize(bool privateSlot)
    {
        var module = SpirvReader.Parse(StoreOnlyFixture(privateSlot).ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void SlotHelperArgumentsRequireCompatibleNativeAddressSpacesAndTypes(bool wrongSpace)
    {
        var binary = Fixture("scalar", "select", "helper-store", false, false);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        int parameter = code.FindIndex(i => (Op)i.Opcode == Op.FunctionParameter);
        uint original = code[parameter].Operands[0]; var pointer = code.Single(i => (Op)i.Opcode == Op.TypePointer && i.Operands[0] == original);
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), new((ushort)Op.TypePointer,
            [next, wrongSpace ? 6u : 7u, wrongSpace ? pointer.Operands[2] : original]));
        parameter = code.FindIndex(i => (Op)i.Opcode == Op.FunctionParameter);
        code[parameter] = code[parameter] with { Operands = [next++, code[parameter].Operands[1]] };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = next, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("transfer type mismatch", error.Message);
    }
}

using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class PointerNullTests
{
    internal static SpirvBinary Fixture(string kind, string mode, bool allNull, bool equality = false, bool privateSlot = false)
    {
        var original = PointerSelectionTests.Fixture(kind, false, false);
        var pointerTypes = original.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        var choice = original.Instructions.First(i => (Op)i.Opcode == Op.Select && pointerTypes.Contains(i.Operands[0])).Operands;
        var binary = mode switch
        {
            "phi" => PointerPhiTests.Fixture(kind, "branch", false),
            "slot" => PointerMemoryTests.Fixture(kind, "select", false, privateSlot),
            "loop" => PointerMemoryTests.Fixture(kind, "swap", false, privateSlot),
            "return" or "nested" => PointerReturnTests.Fixture(kind, "select", false, mode == "nested", true),
            _ => original
        };
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint nullValue = next++;
        uint Source(uint id) => id == choice[4] || allNull && id == choice[3] ? nullValue : id;
        for (int index = 0; index < code.Count; index++)
        {
            var i = code[index]; var a = i.Operands.ToArray();
            switch ((Op)i.Opcode)
            {
                case Op.Select when pointerTypes.Contains(a[0]): a[3] = Source(a[3]); a[4] = Source(a[4]); break;
                case Op.Phi when pointerTypes.Contains(a[0]): for (int p = 2; p < a.Length; p += 2) a[p] = Source(a[p]); break;
                case Op.Store: a[1] = Source(a[1]); break;
                case Op.FunctionCall: for (int p = 3; p < a.Length; p++) a[p] = Source(a[p]); break;
            }
            code[index] = i with { Operands = a };
        }
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), I(Op.ConstantNull, choice[0], nullValue));
        var definitions = code.Where(i => (Op)i.Opcode is Op.Select or Op.Phi or Op.Load or Op.FunctionCall && pointerTypes.Contains(i.Operands[0])).ToDictionary(i => i.Operands[1]);
        bool VariablePointer(uint id) => definitions.TryGetValue(id, out var d) && (Op)d.Opcode is Op.Select or Op.Phi or Op.Load or Op.FunctionCall;
        int first = code.FindIndex(i => (Op)i.Opcode == Op.Load && !pointerTypes.Contains(i.Operands[0]) && VariablePointer(i.Operands[2])
            || (Op)i.Opcode == Op.AtomicIAdd && VariablePointer(i.Operands[2]));
        uint pointer = code[first].Operands[2];
        int end = code.FindIndex(first, i => (Op)i.Opcode is Op.Branch or Op.Return);
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == value).Operands[1];
        uint output = code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, 1]).Operands[0];
        uint outputType = code.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands is [_, 12, var t] && t == uintType).Operands[0];
        uint comparison = next++, nonNull = equality ? next++ : comparison, encoded = next++, address = next++;
        uint accept = next++, reject = next++, merge = next++;
        var body = code.GetRange(first, end - first); code.RemoveRange(first, end - first);
        var guarded = new List<SpirvInstruction> { I(equality ? Op.PtrEqual : Op.PtrNotEqual, boolean, comparison, pointer, nullValue) };
        if (equality) guarded.Add(I(Op.LogicalNot, boolean, nonNull, comparison));
        guarded.AddRange([I(Op.Select, uintType, encoded, nonNull, Constant(1), Constant(0)),
            I(Op.AccessChain, outputType, address, output, Constant(0), Constant(3)), I(Op.Store, address, encoded),
            I(Op.SelectionMerge, merge, 0), I(Op.BranchConditional, nonNull, accept, reject), I(Op.Label, accept)]);
        guarded.AddRange(body); guarded.AddRange([I(Op.Branch, merge), I(Op.Label, reject), I(Op.Branch, merge), I(Op.Label, merge)]);
        code.InsertRange(first, guarded);
        bool function = false; var globals = new List<uint>();
        foreach (var i in code)
        {
            if ((Op)i.Opcode == Op.Function) function = true;
            if (!function && (Op)i.Opcode == Op.Variable) globals.Add(i.Operands[1]);
            if ((Op)i.Opcode == Op.FunctionEnd) function = false;
        }
        int entry = code.FindIndex(i => (Op)i.Opcode == Op.EntryPoint);
        code[entry] = code[entry] with { Operands = [.. code[entry].Operands, .. globals] };
        return new() { Version = 0x10400, Generator = binary.Generator, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData("scalar", "select", false, false, false)] [InlineData("scalar", "select", true, true, false)]
    [InlineData("scalar", "phi", false, true, false)] [InlineData("array", "phi", true, false, false)]
    [InlineData("array", "slot", false, false, false)] [InlineData("scalar", "slot", false, true, true)]
    [InlineData("scalar", "loop", false, true, false)] [InlineData("array", "loop", false, false, true)]
    [InlineData("scalar", "return", false, false, false)] [InlineData("array", "nested", false, true, false)]
    [InlineData("scalar", "nested", true, false, false)] [InlineData("atomic", "select", false, true, false)]
    [InlineData("workgroup", "phi", false, false, false)] [InlineData("workgroup", "loop", false, true, true)]
    [InlineData("atomic", "select", true, false, false)] [InlineData("atomic", "loop", false, false, true)]
    [InlineData("atomic", "nested", false, true, false)] [InlineData("workgroup", "nested", true, true, false)]
    public void NullableAddressesRetainGuardsAndFiniteState(string kind, string mode, bool allNull, bool equality, bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, allNull, equality, privateSlot).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void NullComparisonsRetainVersionAndOperandValidation(bool wrongType)
    {
        var binary = Fixture("scalar", "select", false); var code = binary.Instructions.ToList();
        int compare = code.FindIndex(i => (Op)i.Opcode == Op.PtrNotEqual);
        if (wrongType) code[compare] = code[compare] with { Operands = [.. code[compare].Operands[..3], code.First(i => (Op)i.Opcode == Op.Constant).Operands[1]] };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary
        { Version = wrongType ? binary.Version : 0x10300, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains(wrongType ? "undefined pointer" : "1.4", error.Message);
    }

    [Theory] [InlineData("scalar", 0u)] [InlineData("workgroup", 4441u)]
    public void NullProvenanceRetainsVariablePointerCapabilities(string kind, uint capability)
    {
        var binary = Fixture(kind, "phi", false); var code = binary.Instructions.ToList();
        code.RemoveAll(i => (Op)i.Opcode == Op.Capability && i.Operands[0] is 4441 or 4442);
        if (capability != 0) code.Insert(1, new((ushort)Op.Capability, [capability]));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary
        { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains(capability == 0 ? "variable-pointer capability" : "full VariablePointers", error.Message);
    }

    [Fact]
    public void ARepeatedPointerValueCanBeComparedWithoutAddressEquivalenceAssumptions()
    {
        var binary = Fixture("scalar", "select", false); var code = binary.Instructions.ToList();
        int comparison = code.FindIndex(i => (Op)i.Opcode == Op.PtrNotEqual);
        code[comparison] = code[comparison] with { Operands = [.. code[comparison].Operands[..3], code[comparison].Operands[2]] };
        var module = SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Fact]
    public void AnUnusedNullConstantDoesNotSpecializeUnrelatedUncalledPointerParameters()
    {
        var binary = PointerSelectionTests.Fixture("workgroup", false, false); var code = binary.Instructions.ToList(); uint next = binary.Bound;
        var choice = code.First(i => (Op)i.Opcode == Op.Select).Operands;
        uint voidType = code.First(i => (Op)i.Opcode == Op.TypeVoid).Operands[0];
        uint boolType = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint dataType = code.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands[0] == choice[0]).Operands[2];
        uint signature = next++, nullValue = next++, function = next++, p = next++, q = next++, condition = next++, label = next++, selected = next++, loaded = next++;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), [I(Op.TypeFunction, signature, voidType, choice[0], choice[0], boolType), I(Op.ConstantNull, choice[0], nullValue)]);
        code.AddRange([I(Op.Function, voidType, function, 0, signature), I(Op.FunctionParameter, choice[0], p), I(Op.FunctionParameter, choice[0], q),
            I(Op.FunctionParameter, boolType, condition), I(Op.Label, label), I(Op.Select, choice[0], selected, condition, p, q),
            I(Op.Load, dataType, loaded, selected), I(Op.Return), I(Op.FunctionEnd)]);
        var module = SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = next, Instructions = code }.ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }
}

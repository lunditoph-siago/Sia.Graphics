using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PointerComparisonTests
{
    internal static SpirvBinary Fixture(string kind, string mode, string relation = "other", int indexWidth = 32, bool privateSlot = false, int resultWidth = 32)
    {
        string sourceKind = relation == "self-cross" ? "cross" : kind;
        var original = PointerSelectionTests.Fixture(sourceKind, false, false);
        var choice = original.Instructions.First(i => (Op)i.Opcode == Op.Select).Operands;
        var binary = mode switch
        {
            "phi" => PointerPhiTests.Fixture(sourceKind, "branch", false),
            "slot" => PointerMemoryTests.Fixture(sourceKind, "select", false, privateSlot),
            "loop" => PointerMemoryTests.Fixture(sourceKind, "swap", false, privateSlot),
            "return" or "nested" => PointerReturnTests.Fixture(sourceKind, "select", false, mode == "nested", true),
            _ => original
        };
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == value).Operands[1];
        var added = new List<SpirvInstruction>();
        uint Integer(int width)
        {
            uint bits = (uint)System.Math.Abs(width), sign = width < 0 ? 0u : 1u;
            var existing = code.Concat(added).FirstOrDefault(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, var w, var s] && w == bits && s == sign);
            if (existing is not null) return existing.Operands[0];
            uint result = next++; added.Add(I(Op.TypeInt, result, bits, sign));
            uint capability = bits == 64 ? 11u : bits == 16 ? 22u : 0;
            if (capability != 0 && !code.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == capability)) code.Insert(1, I(Op.Capability, capability));
            return result;
        }
        var left = code.First(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[3]);
        int rightPosition = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[4]);
        var right = code[rightPosition].Operands.ToArray();
        if (relation is not ("different-field" or "self-cross") && kind != "descriptor") right[2] = left.Operands[2];
        if (relation == "constant") right[^1] = Constant(2);
        code[rightPosition] = code[rightPosition] with { Operands = right };
        if (indexWidth != 32)
        {
            uint type = Integer(-indexWidth), cast = next++;
            rightPosition = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[4]);
            var conversions = new List<SpirvInstruction>();
            if (indexWidth < 0 && System.Math.Abs(indexWidth) != 32)
            {
                uint unsigned = Integer(-System.Math.Abs(indexWidth)), intermediate = next++;
                conversions.Add(I(Op.UConvert, unsigned, intermediate, right[^1])); conversions.Add(I(Op.Bitcast, type, cast, intermediate));
            }
            else conversions.Add(I(System.Math.Abs(indexWidth) == 32 ? Op.Bitcast : Op.UConvert, type, cast, right[^1]));
            code.InsertRange(rightPosition, conversions);
            right[^1] = cast; code[rightPosition + conversions.Count] = code[rightPosition + conversions.Count] with { Operands = right };
        }
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        var definitions = code.Where(i => (Op)i.Opcode is Op.Select or Op.Phi or Op.Load or Op.FunctionCall or Op.CopyObject && pointers.Contains(i.Operands[0])).ToDictionary(i => i.Operands[1]);
        int first = code.FindIndex(i => (Op)i.Opcode == Op.Load && !pointers.Contains(i.Operands[0]) && definitions.ContainsKey(i.Operands[2])
            || (Op)i.Opcode == Op.AtomicIAdd && definitions.ContainsKey(i.Operands[2]));
        uint selected = code[first].Operands[2], reference = choice[4];
        bool difference = kind != "vector" && relation is not ("different-field" or "cross-workgroup" or "cross-storage");
        uint differenceType = difference ? Integer(resultWidth) : uintType;
        if (difference && kind != "workgroup") code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeInt), I(Op.Decorate, choice[0], 6, 4));
        first = code.FindIndex(i => (Op)i.Opcode == Op.Load && !pointers.Contains(i.Operands[0]) && definitions.ContainsKey(i.Operands[2])
            || (Op)i.Opcode == Op.AtomicIAdd && definitions.ContainsKey(i.Operands[2]));
        if (relation == "cross-workgroup") { reference = next++; added.Add(I(Op.Variable, choice[0], reference, 4)); }
        uint output = code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, 1]).Operands[0];
        uint outputType = code.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands is [_, 12, var t] && t == uintType).Operands[0];
        int end = code.FindIndex(first, i => (Op)i.Opcode is Op.Branch or Op.Return);
        code.RemoveRange(first, end - first); var body = new List<SpirvInstruction>();
        if (relation is "self" or "self-cross") { reference = next++; body.Add(I(Op.CopyObject, choice[0], reference, selected)); }
        if (relation == "cross-storage") { reference = next++; body.Add(I(Op.AccessChain, choice[0], reference, output, Constant(0), Constant(0))); }
        uint equal = next++, unequal = next++, equalValue = next++, unequalValue = next++, delta = next++, encodedDelta = next++, old = next++;
        body.AddRange([I(Op.PtrEqual, boolean, equal, selected, reference), I(Op.PtrNotEqual, boolean, unequal, selected, reference),
            I(Op.Select, uintType, equalValue, equal, Constant(1), Constant(0)), I(Op.Select, uintType, unequalValue, unequal, Constant(1), Constant(0))]);
        if (difference)
        {
            body.Add(I(Op.PtrDiff, differenceType, delta, selected, reference));
            body.Add(I(System.Math.Abs(resultWidth) == 32 ? Op.Bitcast : Op.UConvert, uintType, encodedDelta, delta));
        }
        else encodedDelta = Constant(0);
        body.Add(kind == "atomic" ? I(Op.AtomicIAdd, uintType, old, selected, Constant(1), Constant(0), Constant(100)) : I(Op.Load, uintType, old, selected));
        var values = new[] { equalValue, unequalValue, encodedDelta, old };
        for (uint position = 0; position < values.Length; position++)
        {
            uint address = next++; body.AddRange([I(Op.AccessChain, outputType, address, output, Constant(0), Constant(position)), I(Op.Store, address, values[position])]);
        }
        if (kind != "atomic" && relation != "self-cross")
        {
            uint changed = next++; body.AddRange([I(Op.IAdd, uintType, changed, old, Constant(100)), I(Op.Store, selected, changed)]);
        }
        code.InsertRange(first, body); code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), added);
        bool function = false; var globals = new List<uint>();
        foreach (var i in code)
        {
            if ((Op)i.Opcode == Op.Function) function = true;
            if (!function && (Op)i.Opcode == Op.Variable) globals.Add(i.Operands[1]);
            if ((Op)i.Opcode == Op.FunctionEnd) function = false;
        }
        int entry = code.FindIndex(i => (Op)i.Opcode == Op.EntryPoint); code[entry] = code[entry] with { Operands = [.. code[entry].Operands, .. globals] };
        return new() { Version = 0x10400, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData("scalar", "select", "other", 32, false, 32)] [InlineData("scalar", "phi", "other", -16, false, 16)]
    [InlineData("scalar", "slot", "constant", 64, true, 64)] [InlineData("scalar", "loop", "other", -32, false, -32)]
    [InlineData("scalar", "return", "other", 32, false, 32)] [InlineData("scalar", "nested", "other", -64, false, 64)]
    [InlineData("atomic", "loop", "other", 32, true, 32)] [InlineData("atomic", "nested", "constant", 32, false, 32)]
    [InlineData("workgroup", "phi", "other", 32, false, 32)] [InlineData("workgroup", "loop", "other", 32, true, 32)]
    [InlineData("workgroup", "nested", "cross-workgroup", 32, false, 32)] [InlineData("vector", "loop", "other", 32, false, 32)]
    [InlineData("scalar", "select", "different-field", 32, false, 32)] [InlineData("scalar", "select", "self-cross", 32, false, 32)]
    public void FinitePointerComparisonsRetainCapturedCoordinates(string kind, string mode, string relation, int indexWidth, bool privateSlot, int resultWidth)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, relation, indexWidth, privateSlot, resultWidth).ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void PotentiallyAliasedStorageBindingsRetainAnExplicitDiagnostic(bool fullCapability)
    {
        var binary = Fixture("scalar", "select", "cross-storage"); var code = binary.Instructions.ToList();
        if (fullCapability) code.Insert(1, new((ushort)Op.Capability, [4442]));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("potentially aliased", error.Message);
    }

    [Fact]
    public void StorageElementDifferenceRetainsItsRequiredPointerStride()
    {
        var binary = Fixture("scalar", "select"); var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Decorate || i.Operands[1] != 6 || !binary.Instructions.Any(t => (Op)t.Opcode == Op.TypePointer && t.Operands[0] == i.Operands[0])).ToList();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("ArrayStride", error.Message);
    }

    internal static SpirvBinary MismatchedTypeFixture(Op operation, uint stride)
    {
        var binary = Fixture("scalar", "select"); var code = binary.Instructions.ToList();
        var comparison = code.First(i => (Op)i.Opcode == operation).Operands;
        var address = code.First(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == comparison[3]).Operands;
        var pointer = code.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands[0] == address[0]).Operands;
        uint duplicate = binary.Bound, reference = duplicate + 1;
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeInt), new((ushort)Op.Decorate, [duplicate, 6, stride]));
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), new((ushort)Op.TypePointer, [duplicate, .. pointer[1..]]));
        int first = code.FindIndex(i => (Op)i.Opcode is Op.PtrEqual or Op.PtrNotEqual or Op.PtrDiff);
        code.Insert(first, new((ushort)Op.AccessChain, [duplicate, reference, .. address[2..]]));
        int index = code.FindIndex(i => (Op)i.Opcode == operation);
        code[index] = code[index] with { Operands = [.. comparison[..3], reference] };
        return new() { Version = binary.Version, Bound = reference + 1, Instructions = code };
    }

    [Theory]
    [InlineData(401, 4u)] [InlineData(401, 8u)]
    [InlineData(402, 4u)] [InlineData(402, 8u)]
    [InlineData(403, 4u)] [InlineData(403, 8u)]
    public void ComparisonOperandsRequireTheSameTypeIdentity(int operation, uint stride)
    {
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(MismatchedTypeFixture((Op)operation, stride).ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("operand type mismatch", error.Message);
    }

    internal static SpirvBinary CrossBindingDifferenceFixture()
    {
        var binary = Fixture("scalar", "select", "cross-storage"); var code = binary.Instructions.ToList();
        var comparison = code.First(i => (Op)i.Opcode == Op.PtrEqual).Operands;
        var reference = code.First(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == comparison[3]).Operands;
        var zero = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [_, _, 0]).Operands;
        uint source = code.First(i => (Op)i.Opcode == Op.AccessChain && i.Operands[0] == reference[0] && i.Operands[^1] == zero[1]).Operands[1];
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeInt), new((ushort)Op.Decorate, [reference[0], 6, 4]));
        int first = code.FindIndex(i => (Op)i.Opcode == Op.PtrEqual);
        code.Insert(first, new((ushort)Op.PtrDiff, [zero[0], binary.Bound, source, comparison[3]]));
        return new() { Version = binary.Version, Bound = binary.Bound + 1, Instructions = code };
    }

    [Fact]
    public void CrossBindingElementDifferencesRequireAddressEquivalence()
    {
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(CrossBindingDifferenceFixture().ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("potentially aliased", error.Message);
    }
}

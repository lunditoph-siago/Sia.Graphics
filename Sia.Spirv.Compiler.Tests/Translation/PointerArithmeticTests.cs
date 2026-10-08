using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PointerArithmeticTests
{
    internal static SpirvBinary Fixture(string kind, string mode, string offset = "forward", int width = 32, bool privateSlot = false)
    {
        var original = PointerComparisonTests.Fixture(kind, mode, "other", 32, privateSlot, 32);
        var choice = PointerSelectionTests.Fixture(kind, false, false).Instructions.First(i => (Op)i.Opcode == Op.Select).Operands;
        var code = original.Instructions.ToList(); uint next = original.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == value).Operands[1];
        uint bits = (uint)System.Math.Abs(width), sign = width > 0 ? 1u : 0;
        var existing = code.FirstOrDefault(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, var w, var s] && w == bits && s == sign);
        var added = new List<SpirvInstruction>(); uint type = existing?.Operands[0] ?? next++;
        if (existing is null) added.Add(I(Op.TypeInt, type, bits, sign));
        if (bits != 32) code.Insert(1, I(Op.Capability, bits == 64 ? 11u : 22u));
        uint minus = next++;
        added.Add(I(Op.Constant, bits == 64 ? [type, minus, uint.MaxValue, uint.MaxValue] : [type, minus, bits == 16 && sign == 0 ? ushort.MaxValue : uint.MaxValue]));
        uint selected = code.First(i => (Op)i.Opcode == Op.PtrEqual).Operands[2];
        int position = offset == "selected"
            ? code.FindIndex(i => i.Operands.Length >= 2 && i.Operands[0] == choice[0] && i.Operands[1] == selected)
            : code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[3]);
        var target = code[position]; var operands = target.Operands.ToArray(); uint baseId = next++, element;
        operands[1] = baseId;
        var body = new List<SpirvInstruction>();
        if (offset is "forward" or "onepast")
        {
            element = offset == "onepast" ? Constant(4) : operands[^1]; operands[^1] = Constant(0);
            if (type != uintType)
            {
                uint cast = next++;
                if (bits != 32 && sign == 1)
                {
                    uint unsigned = next++, intermediate = next++; added.Insert(0, I(Op.TypeInt, unsigned, bits, 0));
                    body.Add(I(Op.UConvert, unsigned, intermediate, element)); body.Add(I(Op.Bitcast, type, cast, intermediate));
                }
                else body.Add(I(bits == 32 ? Op.Bitcast : Op.UConvert, type, cast, element));
                element = cast;
            }
        }
        else element = minus;
        body.Insert(0, target with { Operands = operands });
        uint result = offset == "chained" ? next++ : target.Operands[1];
        body.Add(I(Op.PtrAccessChain, choice[0], result, baseId, element));
        if (offset == "chained") body.Add(I(Op.PtrAccessChain, choice[0], target.Operands[1], result, Constant(1)));
        code.RemoveAt(position); code.InsertRange(position, body);
        if (offset == "onepast")
        {
            var comparison = code.First(i => (Op)i.Opcode == Op.PtrEqual).Operands;
            for (int p = 0; p < code.Count; p++)
            {
                var i = code[p]; var a = i.Operands.ToArray();
                if ((Op)i.Opcode is Op.Load or Op.AtomicIAdd && a[2] == comparison[2]) a[2] = comparison[3];
                if ((Op)i.Opcode == Op.Store && a[0] == comparison[2]) a[0] = comparison[3];
                code[p] = i with { Operands = a };
            }
        }
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), added);
        return new() { Version = original.Version, Bound = next, Instructions = code };
    }

    internal static SpirvBinary AggregateFixture(string offset, int width = 32)
    {
        var original = PointerComparisonTests.Fixture("scalar", "select"); var code = original.Instructions.ToList(); uint next = original.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == value).Operands[1];
        var choice = code.First(i => (Op)i.Opcode == Op.Select).Operands;
        var array = code.First(i => (Op)i.Opcode == Op.TypeArray && i.Operands[1] == uintType && i.Operands[2] == Constant(4));
        uint inner = next++, pointer = next++, baseId = next++, element = next++, length = next++;
        uint bits = (uint)System.Math.Abs(width), sign = width > 0 ? 1u : 0;
        var integer = code.FirstOrDefault(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, var w, var s] && w == bits && s == sign);
        uint elementType = integer?.Operands[0] ?? next++;
        code[code.IndexOf(array)] = array with { Operands = [array.Operands[0], inner, length] };
        int arrayPosition = code.FindIndex(i => (Op)i.Opcode == Op.TypeArray && i.Operands[0] == array.Operands[0]);
        code.InsertRange(arrayPosition, [I(Op.Constant, uintType, length, 2), I(Op.TypeArray, inner, uintType, length)]);
        var added = new List<SpirvInstruction> { I(Op.TypePointer, pointer, 12, inner) };
        if (integer is null) added.Add(I(Op.TypeInt, elementType, bits, sign));
        added.Add(I(Op.Constant, elementType, element, offset == "forward" ? 1u : width == -16 ? ushort.MaxValue : uint.MaxValue));
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), added);
        if (System.Math.Abs(width) == 16) code.Insert(1, I(Op.Capability, 22));
        code.InsertRange(code.FindIndex(i => ((Op)i.Opcode).ToString().StartsWith("Type", StringComparison.Ordinal)), [I(Op.Decorate, inner, 6, 4), I(Op.Decorate, pointer, 6, 8)]);
        int decoration = code.FindIndex(i => (Op)i.Opcode == Op.Decorate && i.Operands is [var target, 6, _] && target == array.Operands[0]);
        code[decoration] = I(Op.Decorate, array.Operands[0], 6, 8);
        foreach (uint id in new[] { choice[3], choice[4] })
        {
            int at = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == id); var a = code[at].Operands;
            if (id == choice[3])
            {
                code[at] = I(Op.AccessChain, [pointer, baseId, .. a[2..^1], Constant(offset == "forward" ? 0u : 1u)]);
                code.Insert(at + 1, I(Op.PtrAccessChain, a[0], a[1], baseId, element, a[^1]));
            }
            // PtrDiff is defined within one innermost array. Compare with an
            // element of the same inner array reached by the arithmetic step.
            else code[at] = I(Op.AccessChain, [.. a[..^1], Constant(offset == "forward" ? 1u : 0u), a[^1]]);
        }
        return new() { Version = original.Version, Bound = next, Instructions = code };
    }

    [Theory] [InlineData("forward", 32)] [InlineData("backward", 32)] [InlineData("backward", -16)]
    public void ArithmeticStepsAggregateElementsBeforeApplyingTrailingIndices(string offset, int width)
    {
        var module = SpirvReader.Parse(AggregateFixture(offset, width).ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    internal static SpirvBinary DirectFixture()
    {
        var original = Fixture("scalar", "select"); var code = original.Instructions.ToList(); var constants = new List<SpirvInstruction>();
        foreach (var i in code.Where(i => (Op)i.Opcode is Op.PtrEqual or Op.PtrNotEqual or Op.PtrDiff).ToArray())
        {
            constants.Add(new((ushort)((Op)i.Opcode == Op.PtrDiff ? Op.Constant : Op.ConstantFalse),
                (Op)i.Opcode == Op.PtrDiff ? [i.Operands[0], i.Operands[1], 0] : i.Operands[..2]));
            code.Remove(i);
        }
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), constants);
        return new() { Version = original.Version, Bound = original.Bound, Instructions = code };
    }

    [Fact]
    public void ArithmeticWithoutComparisonsOrMergedPointersStillNormalizes()
    {
        var module = SpirvReader.Parse(DirectFixture().ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    internal static SpirvBinary ConstantSourcesFixture()
    {
        var original = Fixture("scalar", "select", "selected", -16); var code = original.Instructions.ToList();
        var choice = PointerSelectionTests.Fixture("scalar", false, false).Instructions.First(i => (Op)i.Opcode == Op.Select).Operands;
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        foreach ((uint pointer, uint index) in new[] { (choice[3], 2u), (choice[4], 3u) })
        {
            int at = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == pointer);
            uint literal = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == index).Operands[1];
            var a = code[at].Operands.ToArray(); a[^1] = literal; code[at] = code[at] with { Operands = a };
        }
        return new() { Version = original.Version, Bound = original.Bound, Instructions = code };
    }

    [Fact]
    public void ArithmeticMergesDistinctConstantSourcePathsWithoutDuplicateScalarDefinitions()
    {
        var module = SpirvReader.Parse(ConstantSourcesFixture().ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData("scalar", "select", "forward", 32, false)] [InlineData("scalar", "phi", "forward", 16, false)]
    [InlineData("scalar", "slot", "forward", 64, true)] [InlineData("scalar", "loop", "backward", -16, true)]
    [InlineData("scalar", "return", "chained", -32, false)] [InlineData("scalar", "nested", "selected", -64, false)]
    [InlineData("atomic", "select", "forward", 32, false)] [InlineData("atomic", "loop", "selected", -16, true)]
    [InlineData("workgroup", "phi", "backward", 32, false)] [InlineData("workgroup", "nested", "chained", 64, false)]
    [InlineData("scalar", "select", "onepast", 32, false)] [InlineData("scalar", "phi", "onepast", 64, false)]
    [InlineData("scalar", "slot", "onepast", -16, true)] [InlineData("scalar", "nested", "onepast", -64, false)]
    public void ArrayElementArithmeticRetainsAddressSnapshots(string kind, string mode, string offset, int width, bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, offset, width, privateSlot).ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData("stride")] [InlineData("capability")]
    public void ArithmeticRequiresItsNativeDeclarations(string missing)
    {
        var original = Fixture("scalar", "select");
        var code = original.Instructions.Where(i => missing == "stride" ? (Op)i.Opcode != Op.Decorate || i.Operands[1] != 6
            : (Op)i.Opcode != Op.Capability || i.Operands[0] is not (4441 or 4442)).ToArray();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = original.Version, Bound = original.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains(missing == "stride" ? "ArrayStride" : "variable-pointer capability", error.Message);
    }
}

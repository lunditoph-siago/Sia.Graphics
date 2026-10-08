using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PointerCrossBufferSlotTests
{
    internal static SpirvBinary Fixture(string mode, string transfer, bool privateSlot, bool qualified = false, uint iterations = 3, uint version = 0, bool limited = false)
    {
        var binary = PointerSlotTransferTests.Fixture("cross", mode, transfer, privateSlot, qualified, iterations, version);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        if (limited)
        {
            code.RemoveAll(i => (Op)i.Opcode == Op.Capability && i.Operands is [4442]);
            if (!code.Any(i => (Op)i.Opcode == Op.Capability && i.Operands is [4441])) code.Insert(1, I(Op.Capability, 4441));
        }
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        var types = new List<SpirvInstruction>(); var variables = new Dictionary<uint, List<SpirvInstruction>>();
        foreach (var select in code.Where(i => (Op)i.Opcode == Op.Select && pointers.Contains(i.Operands[0])).ToArray())
        {
            var a = select.Operands; uint type = next++, slot = next++, left = next++, right = next++, merge = next++;
            int index = code.IndexOf(select); uint predecessor = code.Take(index).Last(i => (Op)i.Opcode == Op.Label).Operands[0]; code.RemoveAt(index);
            uint owner = code.Take(index).Last(i => (Op)i.Opcode == Op.Function).Operands[1];
            if (!variables.TryGetValue(owner, out var locals)) variables.Add(owner, locals = []);
            types.Add(I(Op.TypePointer, type, 7, a[0])); locals.Add(I(Op.Variable, type, slot, 7));
            code.InsertRange(index, [I(Op.SelectionMerge, merge, 0), I(Op.BranchConditional, a[2], left, right),
                I(Op.Label, left), I(Op.Store, slot, a[3]), I(Op.Branch, merge),
                I(Op.Label, right), I(Op.Store, slot, a[4]), I(Op.Branch, merge), I(Op.Label, merge), I(Op.Load, a[0], a[1], slot)]);
            for (int p = 0; p < code.Count; p++) if ((Op)code[p].Opcode == Op.Phi)
            {
                var incoming = code[p].Operands.ToArray();
                for (int q = 3; q < incoming.Length; q += 2) if (incoming[q] == predecessor) incoming[q] = merge;
                code[p] = code[p] with { Operands = incoming };
            }
        }
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), types);
        foreach (var pair in variables)
        {
            int function = code.FindIndex(i => (Op)i.Opcode == Op.Function && i.Operands[1] == pair.Key);
            code.InsertRange(code.FindIndex(function, i => (Op)i.Opcode == Op.Label) + 1, pair.Value);
        }
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData("branch", "copy", false)] [InlineData("local", "copy", true)]
    [InlineData("nested", "cycle", false)] [InlineData("swap", "cycle", true)]
    [InlineData("local", "cross", false)] [InlineData("swap", "cross", true)]
    [InlineData("local", "helper-copy", false)] [InlineData("nested", "helper-copy", true)]
    [InlineData("local", "helper-store", false)] [InlineData("swap", "helper-store", true)]
    [InlineData("local", "nested-store", false)] [InlineData("nested", "nested-store", true)]
    [InlineData("local", "helper-edit", false)] [InlineData("local", "helper-edit", true)]
    public void FullCapabilitySlotsMayCarryDifferentStorageBuffers(string mode, string transfer, bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture(mode, transfer, privateSlot).ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void LimitedCapabilityStillRejectsOriginalCrossBufferSelections(bool phi)
    {
        var binary = Fixture("branch", "copy", false, limited: true); var code = binary.Instructions.ToList(); uint next = binary.Bound;
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        int index = code.FindIndex(i => (Op)i.Opcode == Op.Load && pointers.Contains(i.Operands[0]));
        var load = code[index]; uint merge = code.Take(index).Last(i => (Op)i.Opcode == Op.Label).Operands[0];
        var branch = code.Take(index).Last(i => (Op)i.Opcode == Op.BranchConditional);
        uint[] labels = branch.Operands[1..];
        uint Stored(uint label)
        {
            int start = code.FindIndex(i => (Op)i.Opcode == Op.Label && i.Operands[0] == label);
            return code.Skip(start + 1).TakeWhile(i => (Op)i.Opcode != Op.Branch).Last(i => (Op)i.Opcode == Op.Store).Operands[1];
        }
        var originalChoice = new SpirvInstruction((ushort)(phi ? Op.Phi : Op.Select), phi
            ? [load.Operands[0], load.Operands[1], Stored(labels[0]), labels[0], Stored(labels[1]), labels[1]]
            : [load.Operands[0], load.Operands[1], branch.Operands[0], Stored(labels[0]), Stored(labels[1])]);
        // Keep the loaded choice as well as the original Select/Phi.
        var additionalLoad = load with { Operands = [load.Operands[0], next++, .. load.Operands[2..]] };
        if (phi)
        {
            code[index] = additionalLoad;
            code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Label && i.Operands[0] == merge) + 1, originalChoice);
        }
        else { code[index] = originalChoice; code.Insert(index + 1, additionalLoad); }
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = next, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("one storage buffer structure", error.Message);
    }

    [Theory] [InlineData(false, "copy")] [InlineData(true, "copy")]
    [InlineData(false, "helper-store")] [InlineData(true, "nested-store")]
    public void LimitedCapabilityAlsoRejectsDereferencingCrossBufferSlotLoads(bool privateSlot, string transfer)
    {
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(Fixture("local", transfer, privateSlot, limited: true).ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("one storage buffer structure", error.Message);
    }
}

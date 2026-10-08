using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class PointerPhiTests
{
    internal static SpirvBinary Fixture(string kind, string mode, bool qualified, uint iterations = 3)
    {
        var binary = PointerSelectionTests.Fixture(kind == "same" ? "scalar" : kind, mode == "nested", qualified);
        uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        var code = binary.Instructions.ToList();
        if (kind == "same")
        {
            var choice = code.First(i => (Op)i.Opcode == Op.Select).Operands;
            var left = code.Single(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[3]);
            int right = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[4]);
            var operands = code[right].Operands.ToArray(); operands[2] = left.Operands[2];
            code[right] = code[right] with { Operands = operands };
        }
        if (mode is "swap" or "self")
        {
            int position = code.FindIndex(i => (Op)i.Opcode == Op.Select && code.Any(t => (Op)t.Opcode == Op.TypePointer && t.Operands[0] == i.Operands[0]));
            var a = code[position].Operands;
            uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
            uint boolType = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
            uint zero = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands.SequenceEqual(new[] { uintType, i.Operands[1], 0u })).Operands[1];
            uint one = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands.SequenceEqual(new[] { uintType, i.Operands[1], 1u })).Operands[1];
            uint limit = next++; code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), I(Op.Constant, uintType, limit, iterations));
            position++;
            uint entry = code.Take(position).Last(i => (Op)i.Opcode == Op.Label).Operands[0];
            uint init = next++, other = next++, q = next++, count = next++, nextCount = next++, condition = next++;
            uint header = next++, body = next++, continuing = next++, merge = next++;
            var suffix = code.Skip(position + 1).TakeWhile(i => (Op)i.Opcode != Op.Return).ToList();
            var tail = code.Skip(code.FindIndex(position, i => (Op)i.Opcode == Op.FunctionEnd) + 1).ToList();
            code.RemoveRange(position, code.Count - position);
            code.AddRange([
                I(Op.Select, a[0], init, a[2], a[3], a[4]), I(Op.Select, a[0], other, a[2], a[4], a[3]), I(Op.Branch, header),
                I(Op.Label, header), I(Op.Phi, a[0], a[1], init, entry, mode == "swap" ? q : a[1], continuing),
                I(Op.Phi, a[0], q, other, entry, mode == "swap" ? a[1] : q, continuing),
                I(Op.Phi, uintType, count, zero, entry, nextCount, continuing), I(Op.ULessThan, boolType, condition, count, limit),
                I(Op.LoopMerge, merge, continuing, 0), I(Op.BranchConditional, condition, body, merge), I(Op.Label, body)]);
            code.AddRange(suffix);
            code.AddRange([I(Op.Branch, continuing), I(Op.Label, continuing), I(Op.IAdd, uintType, nextCount, count, one),
                I(Op.Branch, header), I(Op.Label, merge), I(Op.Return), I(Op.FunctionEnd)]);
            code.AddRange(tail);
        }
        else
        {
            var pointerTypes = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
            foreach (var select in code.Where(i => (Op)i.Opcode == Op.Select && pointerTypes.Contains(i.Operands[0])).ToArray())
            {
                var a = select.Operands;
                var leftBody = new List<SpirvInstruction>(); var rightBody = new List<SpirvInstruction>();
                if (mode == "local")
                {
                    void Move(uint id, List<SpirvInstruction> target)
                    {
                        var access = code.Single(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == id);
                        foreach (uint index in access.Operands[3..])
                        {
                            var load = code.SingleOrDefault(i => (Op)i.Opcode == Op.Load && i.Operands[1] == index);
                            if (load is not null) { code.Remove(load); target.Add(load); }
                        }
                        code.Remove(access); target.Add(access);
                    }
                    Move(a[3], leftBody); Move(a[4], rightBody);
                }
                uint left = next++, right = next++, merge = next++;
                int position = code.IndexOf(select); code.RemoveAt(position);
                var replacement = new List<SpirvInstruction> { I(Op.SelectionMerge, merge, 0), I(Op.BranchConditional, a[2], left, right), I(Op.Label, left) };
                replacement.AddRange(leftBody); replacement.AddRange([I(Op.Branch, merge), I(Op.Label, right)]);
                replacement.AddRange(rightBody); replacement.AddRange([I(Op.Branch, merge), I(Op.Label, merge), I(Op.Phi, a[0], a[1], a[3], left, a[4], right)]);
                code.InsertRange(position, replacement);
            }
        }
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData("scalar", "branch", false)] [InlineData("scalar", "local", true)]
    [InlineData("array", "nested", true)] [InlineData("workgroup", "branch", false)]
    [InlineData("mixed-struct", "local", true)] [InlineData("vector", "local", false)]
    [InlineData("atomic", "swap", true)] [InlineData("mixed-array", "swap", true)]
    [InlineData("scalar", "swap", false)] [InlineData("scalar", "self", true)]
    [InlineData("same", "branch", false)] [InlineData("same", "local", true)] [InlineData("same", "swap", true)]
    public void MergedPointerAddressesSurviveBranchesAndLoopEdges(string kind, string mode, bool qualified)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, qualified).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData("scalar", 0u, "variable-pointer capability")]
    [InlineData("workgroup", 4441u, "full VariablePointers")]
    public void PointerPhisRequireTheirNativeCapability(string kind, uint capability, string message)
    {
        var binary = Fixture(kind, "branch", false);
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Capability || i.Operands[0] is not (4441 or 4442)).ToList();
        if (capability != 0) code.Insert(1, new((ushort)Op.Capability, [capability]));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains(message, error.Message);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void NormalizationPreservesPhiPredecessorAndOriginalBoundChecks(bool invalidBound)
    {
        var binary = Fixture("scalar", "branch", false);
        var code = binary.Instructions.ToList(); int index = code.FindIndex(i => (Op)i.Opcode == Op.Phi);
        var a = code[index].Operands.ToArray(); if (invalidBound) a[1] = binary.Bound; else a[5] = a[3];
        code[index] = code[index] with { Operands = a };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains(invalidBound ? "original header bound" : "Duplicate phi predecessor", error.Message);
    }
}

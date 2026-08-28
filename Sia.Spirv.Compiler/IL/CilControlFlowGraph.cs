using System.Reflection.Emit;

namespace Sia.Spirv.Compiler.IL;

public sealed record CilControlFlowGraph(IReadOnlyList<CilBasicBlock> Blocks)
{
    internal static CilControlFlowGraph Create(
        IReadOnlyList<CilInstruction> instructions,
        int codeSize)
    {
        if (instructions.Count == 0) {
            return new CilControlFlowGraph([]);
        }

        var instructionOffsets = instructions
            .Select(static instruction => instruction.Offset)
            .ToHashSet();
        var leaders = new SortedSet<int> { instructions[0].Offset };

        foreach (var instruction in instructions) {
            foreach (var target in GetBranchTargets(instruction)) {
                if (!instructionOffsets.Contains(target)) {
                    throw new InvalidDataException(
                        $"Branch at IL_{instruction.Offset:x4} targets invalid offset IL_{target:x4}.");
                }
                leaders.Add(target);
            }

            if (EndsBasicBlock(instruction) && instruction.EndOffset < codeSize) {
                leaders.Add(instruction.EndOffset);
            }
        }

        var leaderArray = leaders.ToArray();
        var blocks = new List<CilBasicBlock>(leaderArray.Length);
        var blockIdsByOffset = leaderArray
            .Select(static (offset, id) => (offset, id))
            .ToDictionary(static pair => pair.offset, static pair => pair.id);

        for (var id = 0; id < leaderArray.Length; id++) {
            var start = leaderArray[id];
            var end = id + 1 < leaderArray.Length ? leaderArray[id + 1] : codeSize;
            var blockInstructions = instructions
                .Where(instruction => instruction.Offset >= start && instruction.Offset < end)
                .ToArray();
            if (blockInstructions.Length == 0) {
                throw new InvalidDataException($"Basic block at IL_{start:x4} is empty.");
            }

            var last = blockInstructions[^1];
            var successors = GetBranchTargets(last)
                .Select(target => blockIdsByOffset[target])
                .ToList();
            if (HasFallthrough(last) && last.EndOffset < codeSize) {
                successors.Add(blockIdsByOffset[last.EndOffset]);
            }

            blocks.Add(new CilBasicBlock(id, start, blockInstructions, successors.Distinct().ToArray()));
        }

        return new CilControlFlowGraph(blocks);
    }

    private static IEnumerable<int> GetBranchTargets(CilInstruction instruction)
    {
        if (instruction.OpCode.OperandType is
            OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget) {
            return [instruction.Operand.GetInt32(instruction.Offset)];
        }
        if (instruction.OpCode.OperandType == OperandType.InlineSwitch) {
            return instruction.Operand.GetSwitchTargets(instruction.Offset);
        }
        return [];
    }

    private static bool HasFallthrough(CilInstruction instruction) =>
        instruction.OpCode.FlowControl is not (
            FlowControl.Branch or
            FlowControl.Return or
            FlowControl.Throw) ||
        instruction.OpCode == OpCodes.Switch;

    private static bool EndsBasicBlock(CilInstruction instruction) =>
        instruction.OpCode.FlowControl is
            FlowControl.Branch or
            FlowControl.Cond_Branch or
            FlowControl.Return or
            FlowControl.Throw;
}

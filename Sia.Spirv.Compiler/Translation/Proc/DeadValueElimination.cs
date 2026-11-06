using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static class DeadValueElimination
{
    internal static void Run(ControlFlowFunction graph)
    {
        var predecessors = ControlFlowAnalysis.Predecessors(graph);
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        var parameters = graph.Blocks.SelectMany(b => b.Parameters.Select((p, index) => (p.Id, Values: predecessors[b.Id].Select(e => e.Edge.Arguments[index]).ToArray())))
            .ToDictionary(p => p.Id, p => p.Values);
        var live = new HashSet<int>(); var pending = new Stack<SsaValue>();
        foreach (var block in graph.Blocks) {
            foreach (var value in block.Terminator!.Operands) pending.Push(value);
            foreach (var instruction in block.Instructions)
                if (instruction.Effects != ShaderEffects.None || instruction.Result is not { } value || !CanonicalTypes.Data(value.Type)) {
                    if (instruction.Result is { } result) pending.Push(result);
                    foreach (var operand in instruction.Operation.Operands) pending.Push(operand);
                }
        }
        while (pending.TryPop(out var value)) {
            if (!live.Add(value.Id)) continue;
            if (definitions.TryGetValue(value.Id, out var definition))
                foreach (var operand in definition.Operation.Operands) pending.Push(operand);
            else if (parameters.TryGetValue(value.Id, out var incoming))
                foreach (var operand in incoming) pending.Push(operand);
        }
        foreach (var block in graph.Blocks) {
            block.Instructions.RemoveAll(i => i.Result is { } value && CanonicalTypes.Data(value.Type)
                && i.Effects == ShaderEffects.None && !live.Contains(value.Id));
            for (int p = block.Parameters.Count - 1; p >= 0; p--)
                if (CanonicalTypes.Data(block.Parameters[p].Type) && !live.Contains(block.Parameters[p].Id)) {
                    block.Parameters.RemoveAt(p);
                    foreach (var edge in predecessors[block.Id]) edge.Edge.Arguments.RemoveAt(p);
                }
        }
    }
}

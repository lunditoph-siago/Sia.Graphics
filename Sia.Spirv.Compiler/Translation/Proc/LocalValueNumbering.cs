using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static class LocalValueNumbering
{
    internal static void Run(ControlFlowFunction graph, ControlFlowAnalysisContext? analyses = null)
    {
        analyses?.RequireFunction(graph);
        var replacements = new Dictionary<int, SsaValue>();
        var aliases = new Dictionary<int, SsaValue>();
        SsaValue Resolve(SsaValue value) {
            while (replacements.TryGetValue(value.Id, out var replacement)) value = replacement;
            return value;
        }
        SsaValue Number(SsaValue value) {
            value = Resolve(value);
            while (aliases.TryGetValue(value.Id, out var alias)) value = Resolve(alias);
            return value;
        }
        foreach (var block in graph.Blocks) {
            var available = new Dictionary<(ShaderType Type, ValueOperation Operation, uint RuntimeOperands), SsaValue>();
            var original = block.Instructions.ToArray(); block.Instructions.Clear();
            foreach (var instruction in original) {
                var operation = instruction.Operation.Map(Resolve);
                if (instruction.Result is { } alias && operation is ValueOperation.Let let) {
                    aliases.Add(alias.Id, Number(let.Value));
                }
                if (instruction.Result is { } result && instruction.Effects == ShaderEffects.None
                    && (CanonicalTypes.Data(result.Type) || result.Type is ShaderType.Pointer)
                    && operation is ValueOperation.Literal or ValueOperation.Symbol or ValueOperation.Unary
                        or ValueOperation.Binary or ValueOperation.Convert or ValueOperation.Select
                        or ValueOperation.Access or ValueOperation.Member or ValueOperation.Swizzle) {
                    uint runtimeOperands = 0;
                    foreach (var operand in operation.Operands)
                        runtimeOperands = (runtimeOperands << 1) | (aliases.ContainsKey(Resolve(operand).Id) ? 1u : 0u);
                    var key = (result.Type, Key(operation.Map(Number)), runtimeOperands);
                    if (available.TryGetValue(key, out var value)) { replacements.Add(result.Id, value); continue; }
                    available.Add(key, result);
                }
                block.Instructions.Add(instruction with { Operation = operation });
            }
        }
        foreach (var block in graph.Blocks) {
            for (int i = 0; i < block.Instructions.Count; i++)
                block.Instructions[i] = block.Instructions[i] with { Operation = block.Instructions[i].Operation.Map(Resolve) };
            block.Terminator = block.Terminator!.Map(Resolve);
            foreach (var edge in block.Terminator.Edges)
                for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Resolve(edge.Arguments[i]);
        }
        analyses?.Preserve(ControlFlowAnalyses.All);
    }

    private static ValueOperation Key(ValueOperation operation) => operation is ValueOperation.Literal literal
        ? literal.Value switch {
            Half value => new ValueOperation.Literal(BitConverter.HalfToUInt16Bits(value)),
            float value => new ValueOperation.Literal(BitConverter.SingleToUInt32Bits(value)),
            double value => new ValueOperation.Literal(BitConverter.DoubleToUInt64Bits(value)),
            _ => operation
        } : operation;
}

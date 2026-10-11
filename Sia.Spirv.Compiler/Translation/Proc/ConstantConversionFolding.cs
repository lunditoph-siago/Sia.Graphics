using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

// Preserve the established constant-width conversion behavior before target
// reconstruction introduces temporaries. Other runtime arithmetic stays intact.
internal static class ConstantConversionFolding
{
    internal static void Run(ControlFlowFunction graph)
    {
        var constants = graph.Blocks.SelectMany(b => b.Instructions)
            .Where(i => i.Result is not null && i.Operation is ValueOperation.Literal)
            .ToDictionary(i => i.Result!.Value.Id, i => new Expression.Literal(((ValueOperation.Literal)i.Operation).Value, i.Result!.Value.Type));
        foreach (var block in graph.Blocks)
            for (int index = 0; index < block.Instructions.Count; index++) {
                var instruction = block.Instructions[index];
                if (instruction.Result is not { } result || instruction.Operation is not ValueOperation.Convert conversion
                    || !constants.TryGetValue(conversion.Operand.Id, out var operand)) continue;
                if (!ConstantEvaluator.TryEvaluateRuntime(new Expression.Convert(result.Type, operand, conversion.Bitcast), out var folded)
                    || folded is not Expression.Literal literal) continue;
                block.Instructions[index] = instruction with { Operation = new ValueOperation.Literal(literal.Value) };
                constants[result.Id] = literal;
            }
    }
}

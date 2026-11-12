using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static class UndefinedValueLowering
{
    internal static ControlFlowFunction Run(ControlFlowFunction input)
    {
        if (!input.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Undefined)) return input;
        var output = input.Copy();
        foreach (var block in output.Blocks)
            for (int i = 0; i < block.Instructions.Count; i++)
                if (block.Instructions[i].Operation is ValueOperation.Undefined)
                    block.Instructions[i] = block.Instructions[i] with { Operation = new ValueOperation.Construct([]) };
        return output;
    }

    internal static CanonicalModule Run(CanonicalModule input)
    {
        var known = new Dictionary<string, Expression>(StringComparer.Ordinal);
        bool changed = false;
        Expression Changed(Expression value) { changed = true; return value; }
        Expression Value(Expression value) => value switch {
            Expression.Undefined => Changed(new Expression.Construct(value.Type, []) { Span = value.Span }),
            Expression.Reference r when known.TryGetValue(r.Name, out var constant) => Changed(constant with { Span = r.Span }),
            Expression.Unary u => u with { Operand = Value(u.Operand) },
            Expression.Binary b => b with { Left = Value(b.Left), Right = Value(b.Right) },
            Expression.Construct c => c with { Components = c.Components.Select(Value).ToArray() },
            Expression.Convert c => c with { Operand = Value(c.Operand) },
            Expression.Access a => a with { Base = Value(a.Base), Index = Value(a.Index) },
            Expression.Member m => m with { Base = Value(m.Base) },
            Expression.Swizzle s => s with { Vector = Value(s.Vector) },
            Expression.Select s => s with { Condition = Value(s.Condition), Accept = Value(s.Accept), Reject = Value(s.Reject) },
            Expression.Call c => c with { Arguments = c.Arguments.Select(Value).ToArray() },
            _ => value
        };
        var declarations = input.Declarations;
        var output = new Module { VulkanMemoryModel = declarations.VulkanMemoryModel, WorkgroupInitializationRequired = declarations.WorkgroupInitializationRequired };
        output.Structures.AddRange(declarations.Structures); output.Functions.AddRange(declarations.Functions);
        output.Enables.UnionWith(declarations.Enables); output.DiagnosticFilters.AddRange(declarations.DiagnosticFilters);
        foreach (var constant in declarations.Constants) {
            changed = false;
            var value = constant.Value is null ? null : Value(constant.Value);
            var replacement = constant with { Value = value };
            if (changed && !constant.IsOverride && value is not null && ConstantEvaluator.TryEvaluateRuntime(value, out var folded)) {
                value = folded with { Span = value.Span }; known.Add(constant.Name, value);
                replacement = replacement with { Value = value, IsSpecialization = false };
            }
            output.Constants.Add(replacement);
        }
        output.Globals.AddRange(declarations.Globals.Select(g => g.Initializer is null ? g : g with { Initializer = Value(g.Initializer) }));
        return input with { Declarations = output, Functions = input.Functions.ToDictionary(p => p.Key, p => Run(p.Value), StringComparer.Ordinal) };
    }
}

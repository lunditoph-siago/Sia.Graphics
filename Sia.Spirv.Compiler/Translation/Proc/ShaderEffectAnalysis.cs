using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Per-compilation conservative intrinsic requirements, including helper call closure.
/// Read/write alias analysis is not used to make a user call pure.</summary>
internal static class ShaderEffectAnalysis
{
    /// <summary>Derive requirements from authoritative graphs and their call closure.
    /// Explicitly deferred bodies retain the structured frontend's conservative facts.</summary>
    public static Dictionary<string, ShaderEffects> Compute(CanonicalModule module)
        => Compute(module.Declarations, module.Functions);

    public static Dictionary<string, ShaderEffects> Compute(Module module)
        => Compute(module, null);

    private static Dictionary<string, ShaderEffects> Compute(Module module, IReadOnlyDictionary<string, ControlFlowFunction>? graphs)
    {
        var functions = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var effects = new Dictionary<string, ShaderEffects>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        ShaderEffects Function(string name) {
            if (effects.TryGetValue(name, out var result)) return result;
            if (!active.Add(name)) return ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall;
            if (graphs?.TryGetValue(name, out var graph) == true) {
                result = graph.Blocks.Aggregate(ShaderEffects.None, (value, block) => value | block.Terminator!.Effects
                    | block.Instructions.Aggregate(ShaderEffects.None, (value, instruction) => value | instruction.Effects));
                foreach (var call in graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>())
                    if (functions.ContainsKey(call.Function)) result |= Function(call.Function);
            }
            else result = Body(functions[name].Body);
            active.Remove(name); effects.Add(name, result); return result;
        }
        ShaderEffects Expr(Expression expression) {
            ShaderEffects result = expression switch {
                Expression.HelperInvocation => ShaderEffects.Convergent | ShaderEffects.ReadInvocationState,
                Expression.Call call => (call.Binding != CallBinding.Builtin && functions.ContainsKey(call.Function) ? Function(call.Function) : ShaderBuiltinEffects.For(call.Function))
                    | ShaderBuiltinEffects.Memory(call.MemoryAccess) | ShaderBuiltinEffects.AtomicMemory(call.AtomicMemory),
                Expression.Load load => ShaderEffects.ReadMemory | ShaderBuiltinEffects.Memory(load.MemoryAccess), _ => ShaderEffects.None
            };
            IEnumerable<Expression> children = expression switch {
                Expression.Load l => [l.Pointer], Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right],
                Expression.Call c => c.Arguments, Expression.Construct c => c.Components, Expression.Convert c => [c.Operand],
                Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
                Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
            };
            foreach (var child in children) result |= Expr(child);
            return result;
        }
        ShaderEffects Body(Block block) {
            var result = ShaderEffects.None;
            foreach (var statement in block.Statements) {
                result |= statement switch {
                    Statement.Declare { Initializer: { } value } => Expr(value),
                    Statement.Store store => ShaderEffects.WriteMemory | ShaderBuiltinEffects.Memory(store.MemoryAccess) | Expr(store.Target) | Expr(store.Value),
                    Statement.Evaluate evaluate => Expr(evaluate.Value), Statement.Nested nested => Body(nested.Body),
                    Statement.MeshStore store => ShaderEffects.WriteMemory | Expr(store.Index) | Expr(store.Value),
                    Statement.MeshSetOutputs counts => ShaderEffects.WriteMemory | ShaderEffects.Convergent | Expr(counts.Vertices) | Expr(counts.Primitives),
                    Statement.TaskDispatch dispatch => ShaderBuiltinEffects.Barrier(true) | ShaderEffects.InvocationTermination | Expr(dispatch.Dimensions),
                    Statement.If branch => Expr(branch.Condition) | Body(branch.Accept) | Body(branch.Reject),
                    Statement.Loop loop => Body(loop.Body) | Body(loop.Continuing) | (loop.BreakIf is null ? ShaderEffects.None : Expr(loop.BreakIf)),
                    Statement.Switch selection => Expr(selection.Selector) | selection.Cases.Aggregate(ShaderEffects.None, (e, c) => e | Body(c.Body)),
                    Statement.Return { Value: { } value } => Expr(value), Statement.Barrier => ShaderBuiltinEffects.Barrier(true),
                    Statement.MemoryBarrier => ShaderBuiltinEffects.Barrier(false), Statement.Kill => ShaderEffects.Convergent | ShaderEffects.HelperDemotion,
                    Statement.InvocationKill => ShaderEffects.Convergent | ShaderEffects.InvocationTermination,
                    Statement.Declare or Statement.Return or Statement.Unreachable or Statement.Break or Statement.Continue => ShaderEffects.None,
                    _ => ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall
                };
            }
            return result;
        }
        foreach (string name in functions.Keys) _ = Function(name);
        return effects;
    }
}

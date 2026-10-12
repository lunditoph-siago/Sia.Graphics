using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Expose proven entry builtin values without assuming shader-language uniformity exceptions.</summary>
internal static class WgslEntryPointLowering
{
    public static Module Run(Module input)
    {
        var output = Run(input, new HashSet<string>(StringComparer.Ordinal));
        var zeros = PrivateZeroFacts(output);
        return zeros.Count == 0 ? output : Run(output, zeros);
    }

    // Native entry wrappers may transfer a builtin through a private IO slot.
    // Fold only zero-initialized u32 slots whose every write is zero and whose address never escapes.
    private static HashSet<string> PrivateZeroFacts(Module input)
    {
        bool Zero(Expression e) => ConstantEvaluator.TryEvaluateRuntime(e, out var value) && value is Expression.Literal { Value: uint n } && n == 0;
        var candidates = input.Globals.Where(g => g.Space == AddressSpace.Private && g.Type == ShaderType.U32
            && g.MemoryDecorations == MemoryDecorations.None && (g.Initializer is null || Zero(g.Initializer))).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        void Expr(Expression e) {
            if (e is Expression.Reference { Type: ShaderType.Pointer } pointer) { candidates.Remove(pointer.Name); return; }
            if (e is Expression.Load { Pointer: Expression.Reference root } load) {
                if (load.MemoryAccess is not null) candidates.Remove(root.Name);
                return;
            }
            IEnumerable<Expression> children = e switch {
                Expression.Load l => [l.Pointer], Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right],
                Expression.Call c => c.Arguments, Expression.Construct c => c.Components, Expression.Convert c => [c.Operand],
                Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
                Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
            };
            foreach (var child in children) Expr(child);
        }
        void Body(Block block) {
            foreach (var statement in block.Statements) {
                switch (statement) {
                    case Statement.Declare d: candidates.Remove(d.Name); if (d.Initializer is { } value) Expr(value); break;
                    case Statement.Store s:
                        if (s.Target is Expression.Reference root && candidates.Contains(root.Name)) {
                            if (s.MemoryAccess is not null || !Zero(s.Value)) candidates.Remove(root.Name);
                        }
                        else Expr(s.Target);
                        Expr(s.Value); break;
                    case Statement.Evaluate e: Expr(e.Value); break;
                    case Statement.Return { Value: { } returned }: Expr(returned); break;
                    case Statement.Nested n: Body(n.Body); break;
                    case Statement.If i: Expr(i.Condition); Body(i.Accept); Body(i.Reject); break;
                    case Statement.Loop l: Body(l.Body); Body(l.Continuing); if (l.BreakIf is { } condition) Expr(condition); break;
                    case Statement.Switch s: Expr(s.Selector); foreach (var arm in s.Cases) Body(arm.Body); break;
                }
            }
        }
        foreach (var function in input.Functions) {
            candidates.ExceptWith(function.Arguments.Select(a => a.Name)); Body(function.Body);
        }
        return candidates;
    }

    private static Module Run(Module input, HashSet<string> privateZeros)
    {
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Globals.AddRange(input.Globals); output.Constants.AddRange(input.Constants); output.Structures.AddRange(input.Structures);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions) {
            bool one = function.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh && function.WorkgroupSize.All(e =>
                ConstantEvaluator.TryEvaluateRuntime(new Expression.Convert(ShaderType.U32, e), out var value) && value is Expression.Literal { Value: uint n } && n == 1);
            if (!one && privateZeros.Count == 0) { output.Functions.Add(function); continue; }
            var arguments = (one ? function.Arguments : []).ToDictionary(a => a.Name, StringComparer.Ordinal);
            Expression? Zero(ShaderType type, IoBinding? binding) => binding?.Builtin is "local_invocation_id" or "local_invocation_index"
                ? type is ShaderType.Vector vector ? new Expression.Construct(type, Enumerable.Range(0, vector.Size).Select(_ => (Expression)Expression.U32(0)).ToArray())
                    : new Expression.Literal(0u, type) : null;
            Expression Expr(Expression e) {
                if (e is Expression.Reference referenceZero && privateZeros.Contains(referenceZero.Name) && e.Type == ShaderType.U32
                    || e is Expression.Load { Pointer: Expression.Reference pointerZero } && privateZeros.Contains(pointerZero.Name))
                    return new Expression.Literal(0u, ShaderType.U32) { Span = e.Span };
                if (e is Expression.Reference reference && arguments.TryGetValue(reference.Name, out var argument) && Zero(argument.Type, argument.Binding) is { } zero)
                    return zero with { Span = e.Span };
                if (e is Expression.Member { Base: Expression.Reference root } member && arguments.TryGetValue(root.Name, out argument)
                    && argument.Type is ShaderType.Structure structure && structure.Members.FirstOrDefault(m => m.Name == member.Name) is { } field
                    && Zero(field.Type, field.Binding) is { } component) return component with { Span = e.Span };
                return e switch {
                    Expression.Load l => l with { Pointer = Expr(l.Pointer) }, Expression.Unary u => u with { Operand = Expr(u.Operand) },
                    Expression.Binary b => b with { Left = Expr(b.Left), Right = Expr(b.Right) }, Expression.Call c => c with { Arguments = c.Arguments.Select(Expr).ToArray() },
                    Expression.Construct c => c with { Components = c.Components.Select(Expr).ToArray() }, Expression.Convert c => c with { Operand = Expr(c.Operand) },
                    Expression.Access a => a with { Base = Expr(a.Base), Index = Expr(a.Index) }, Expression.Member m => m with { Base = Expr(m.Base) },
                    Expression.Swizzle s => s with { Vector = Expr(s.Vector) }, Expression.Select s => new Expression.Select(Expr(s.Condition), Expr(s.Accept), Expr(s.Reject)) { Span = s.Span }, _ => e
                };
            }
            Block Body(Block body, bool scoped = true) {
                var saved = arguments.ToArray(); var result = new Block();
                result.DiagnosticFilters.AddRange(body.DiagnosticFilters);
                foreach (var statement in body.Statements) {
                    Statement mapped;
                    if (statement is Statement.Declare d) { mapped = d with { Initializer = d.Initializer is null ? null : Expr(d.Initializer) }; arguments.Remove(d.Name); }
                    else if (statement is Statement.Loop loop) {
                        var before = arguments.ToArray(); var loopBody = Body(loop.Body, false); var continuing = Body(loop.Continuing, false);
                        mapped = loop with { Body = loopBody, Continuing = continuing, BreakIf = loop.BreakIf is null ? null : Expr(loop.BreakIf) };
                        arguments.Clear(); foreach (var item in before) arguments.Add(item.Key, item.Value);
                    }
                    else mapped = statement switch {
                        Statement.Nested n => n with { Body = Body(n.Body) }, Statement.Store s => s with {
                            Target = s.Target is Expression.Reference root && privateZeros.Contains(root.Name) ? s.Target : Expr(s.Target), Value = Expr(s.Value) },
                        Statement.Evaluate e => e with { Value = Expr(e.Value) }, Statement.If i => i with { Condition = Expr(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) },
                        Statement.Switch s => s with { Selector = Expr(s.Selector), Cases = s.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray() },
                        Statement.Return r => r with { Value = r.Value is null ? null : Expr(r.Value) }, _ => statement
                    };
                    result.Statements.Add(mapped);
                }
                if (scoped) { arguments.Clear(); foreach (var item in saved) arguments.Add(item.Key, item.Value); }
                return result;
            }
            var copy = new ShaderFunction(function.Name) {
                Stage = function.Stage, ReturnType = function.ReturnType, ReturnBinding = function.ReturnBinding,
                TaskPayload = function.TaskPayload, MeshOutput = function.MeshOutput, WorkgroupSize = function.WorkgroupSize.ToArray(),
                EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth, Body = Body(function.Body)
            };
            copy.Arguments.AddRange(function.Arguments); copy.DiagnosticFilters.AddRange(function.DiagnosticFilters); output.Functions.Add(copy);
        }
        return output;
    }
}

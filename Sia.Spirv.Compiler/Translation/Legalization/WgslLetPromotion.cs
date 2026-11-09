using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Keep single-assignment structured values immutable when their lexical scope permits it.</summary>
internal static class WgslLetPromotion
{
    private sealed class Scope(Scope? parent) { internal Scope? Parent { get; } = parent; }
    private sealed class Candidate(Statement.Declare declaration)
    {
        internal Statement.Declare Declaration { get; } = declaration;
        internal List<(Scope Scope, int Order)> Writes { get; } = [];
        internal List<(Scope Scope, int Order)> Reads { get; } = [];
        internal bool Escapes;
    }

    internal static Module Run(Module input)
    {
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Globals.AddRange(input.Globals); output.Constants.AddRange(input.Constants);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions) {
            var copy = new ShaderFunction(function.Name) { Stage = function.Stage, ReturnType = function.ReturnType, ReturnBinding = function.ReturnBinding,
                WorkgroupSize = function.WorkgroupSize.ToArray(), TaskPayload = function.TaskPayload, MeshOutput = function.MeshOutput,
                EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth, Body = Body(function, input) };
            copy.Arguments.AddRange(function.Arguments); copy.DiagnosticFilters.AddRange(function.DiagnosticFilters); output.Functions.Add(copy);
        }
        return output;
    }

    private static IEnumerable<Block> Children(Statement statement) => statement switch {
        Statement.Nested n => [n.Body], Statement.If i => [i.Accept, i.Reject], Statement.Loop l => [l.Body, l.Continuing],
        Statement.Switch s => s.Cases.Select(c => c.Body), _ => []
    };
    private static IEnumerable<Statement> Statements(Block block) => block.Statements.SelectMany(s => new[] { s }.Concat(Children(s).SelectMany(Statements)));
    private static IEnumerable<Expression> Operands(Expression expression) => expression switch {
        Expression.Load l => [l.Pointer], Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right],
        Expression.Convert c => [c.Operand], Expression.Construct c => c.Components, Expression.Call c => c.Arguments,
        Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
        Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
    };

    private static Block Body(ShaderFunction function, Module module)
    {
        var reserved = module.Globals.Select(g => g.Name).Concat(module.Constants.Select(c => c.Name)).Concat(function.Arguments.Select(a => a.Name)).ToHashSet(StringComparer.Ordinal);
        var candidates = Statements(function.Body).OfType<Statement.Declare>().GroupBy(d => d.Name)
            .Where(g => g.Count() == 1 && !reserved.Contains(g.Key) && g.First() is { Mutable: true, Initializer: null } d
                && CanonicalTypes.Data(d.Type) && ShaderMemoryRequirements.TypeMemory(d.Type) == MemoryDecorations.None)
            .ToDictionary(g => g.Key, g => new Candidate(g.First()), StringComparer.Ordinal);
        int order = 0;
        void Read(Expression expression, Scope scope, bool escape = false) {
            if (expression is Expression.Reference reference && candidates.TryGetValue(reference.Name, out var candidate)) {
                candidate.Reads.Add((scope, order)); candidate.Escapes |= escape;
            }
            foreach (var operand in Operands(expression)) Read(operand, scope, escape || expression is Expression.Unary { Operator: "&" }
                or Expression.Load { MemoryAccess: not null });
        }
        void Inspect(Block block, Scope scope) {
            foreach (var statement in block.Statements) {
                order++;
                switch (statement) {
                    case Statement.Declare { Initializer: { } value }: Read(value, scope); break;
                    case Statement.Store store:
                        Read(store.Value, scope);
                        if (store.Target is Expression.Reference target && candidates.TryGetValue(target.Name, out var candidate)) {
                            candidate.Writes.Add((scope, order)); candidate.Escapes |= store.MemoryAccess is not null;
                        }
                        else Read(store.Target, scope, escape: true); // Partial writes and address uses retain storage.
                        break;
                    case Statement.Evaluate e: Read(e.Value, scope); break;
                    case Statement.Return { Value: { } value }: Read(value, scope); break;
                    case Statement.If i: Read(i.Condition, scope); break;
                    case Statement.Switch s: Read(s.Selector, scope); break;
                    case Statement.MeshStore s: Read(s.Index, scope); Read(s.Value, scope); break;
                    case Statement.MeshSetOutputs s: Read(s.Vertices, scope); Read(s.Primitives, scope); break;
                    case Statement.TaskDispatch d: Read(d.Dimensions, scope); break;
                }
                foreach (var child in Children(statement)) {
                    var childScope = new Scope(scope); Inspect(child, childScope);
                    if (statement is Statement.Loop loop && ReferenceEquals(child, loop.Continuing) && loop.BreakIf is { } condition) { order++; Read(condition, childScope); }
                }
            }
        }
        Inspect(function.Body, new(null));
        bool Contains(Scope ancestor, Scope scope) {
            for (Scope? current = scope; current is not null; current = current.Parent) if (ReferenceEquals(ancestor, current)) return true;
            return false;
        }
        var promoted = candidates.Where(p => !p.Value.Escapes && p.Value.Writes.Count == 1
            && p.Value.Reads.All(r => r.Order > p.Value.Writes[0].Order && Contains(p.Value.Writes[0].Scope, r.Scope)))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Expression E(Expression expression) {
            if (expression is Expression.Reference reference && promoted.TryGetValue(reference.Name, out var candidate))
                return new Expression.Reference(reference.Name, candidate.Declaration.Type) { Span = reference.Span };
            if (expression is Expression.Load load) {
                var pointer = E(load.Pointer); return pointer.Type is ShaderType.Pointer ? load with { Pointer = pointer } : pointer;
            }
            if (expression is Expression.Access access) {
                var parent = E(access.Base);
                var type = access.Type is ShaderType.Pointer pointer && parent.Type is not ShaderType.Pointer ? pointer.Base : access.Type;
                return new Expression.Access(parent, E(access.Index), type) { Span = access.Span };
            }
            if (expression is Expression.Member member) {
                var parent = E(member.Base);
                var type = member.Type is ShaderType.Pointer pointer && parent.Type is not ShaderType.Pointer ? pointer.Base : member.Type;
                return new Expression.Member(parent, member.Name, type) { Span = member.Span };
            }
            return expression switch {
                Expression.Unary u => u with { Operand = E(u.Operand) }, Expression.Binary b => b with { Left = E(b.Left), Right = E(b.Right) },
                Expression.Convert c => c with { Operand = E(c.Operand) }, Expression.Construct c => c with { Components = c.Components.Select(E).ToArray() },
                Expression.Call c => c with { Arguments = c.Arguments.Select(E).ToArray() },
                Expression.Swizzle s => s with { Vector = E(s.Vector) }, Expression.Select s => s with { Condition = E(s.Condition), Accept = E(s.Accept), Reject = E(s.Reject) }, _ => expression
            };
        }
        Block Rewrite(Block input) {
            var output = new Block(); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
            foreach (var statement in input.Statements) {
                if (statement is Statement.Declare declaration && promoted.ContainsKey(declaration.Name)) continue;
                Statement mapped = statement switch {
                    Statement.Store { Target: Expression.Reference target } store when promoted.TryGetValue(target.Name, out var candidate)
                        => new Statement.Declare(target.Name, candidate.Declaration.Type, E(store.Value), false),
                    Statement.Store store => store with { Target = E(store.Target), Value = E(store.Value) },
                    Statement.Declare d => d with { Initializer = d.Initializer is null ? null : E(d.Initializer) },
                    Statement.Evaluate e => e with { Value = E(e.Value) }, Statement.Return r => r with { Value = r.Value is null ? null : E(r.Value) },
                    Statement.Nested n => n with { Body = Rewrite(n.Body) }, Statement.If i => i with { Condition = E(i.Condition), Accept = Rewrite(i.Accept), Reject = Rewrite(i.Reject) },
                    Statement.Loop l => l with { Body = Rewrite(l.Body), Continuing = Rewrite(l.Continuing), BreakIf = l.BreakIf is null ? null : E(l.BreakIf) },
                    Statement.Switch s => s with { Selector = E(s.Selector), Cases = s.Cases.Select(c => c with { Body = Rewrite(c.Body) }).ToArray() },
                    Statement.MeshStore s => s with { Index = E(s.Index), Value = E(s.Value) }, Statement.MeshSetOutputs s => s with { Vertices = E(s.Vertices), Primitives = E(s.Primitives) },
                    Statement.TaskDispatch d => d with { Dimensions = E(d.Dimensions) }, _ => statement
                };
                output.Statements.Add(mapped with { Span = statement.Span });
            }
            return output;
        }
        return Rewrite(function.Body);
    }
}

using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class WgslWriter
{
    private sealed partial class Writer
    {
        private static IEnumerable<Statement> InlinePendingArrayArguments(IReadOnlyList<Statement> statements)
        {
            for (int i = 0; i < statements.Count; i++)
            {
                // WGSL cannot declare a let with an override-sized array, but
                // it can pass a loaded array by value. A single argument call
                // immediately after its snapshot has the same evaluation order.
                if (statements[i] is Statement.Declare { Mutable: false, Type: ShaderType.Array { OverrideLength: not null }, Initializer: { } value } snapshot
                    && i + 1 < statements.Count && statements.Skip(i + 1).Sum(s => References(s, snapshot.Name)) == 1)
                {
                    var next = statements[i + 1];
                    Expression? expression = next switch { Statement.Declare d => d.Initializer, Statement.Evaluate e => e.Value, _ => null };
                    if (expression is Expression.Call { Arguments: [Expression.Reference r] } call && r.Name == snapshot.Name)
                    {
                        var replacement = call with { Arguments = new[] { value } };
                        yield return next switch
                        {
                            Statement.Declare d => d with { Initializer = replacement },
                            Statement.Evaluate e => e with { Value = replacement },
                            _ => next
                        };
                        i++; continue;
                    }
                }
                yield return statements[i];
            }
        }

        private static int References(Expression value, string name) => value switch
        {
            Expression.Reference r => r.Name == name ? 1 : 0,
            Expression.Load l => References(l.Pointer, name),
            Expression.Unary u => References(u.Operand, name),
            Expression.Binary b => References(b.Left, name) + References(b.Right, name),
            Expression.Call c => c.Arguments.Sum(a => References(a, name)),
            Expression.Construct c => c.Components.Sum(a => References(a, name)),
            Expression.Convert c => References(c.Operand, name),
            Expression.Access a => References(a.Base, name) + References(a.Index, name),
            Expression.Member m => References(m.Base, name),
            Expression.Swizzle s => References(s.Vector, name),
            Expression.Select s => References(s.Condition, name) + References(s.Accept, name) + References(s.Reject, name),
            _ => 0
        };

        private static int References(Block block, string name) => block.Statements.Sum(s => References(s, name));
        private static int References(Statement statement, string name) => statement switch
        {
            Statement.Declare d => d.Initializer is null ? 0 : References(d.Initializer, name),
            Statement.Store s => References(s.Target, name) + References(s.Value, name),
            Statement.Evaluate e => References(e.Value, name),
            Statement.Nested n => References(n.Body, name),
            Statement.If i => References(i.Condition, name) + References(i.Accept, name) + References(i.Reject, name),
            Statement.Loop l => References(l.Body, name) + References(l.Continuing, name) + (l.BreakIf is null ? 0 : References(l.BreakIf, name)),
            Statement.Switch s => References(s.Selector, name) + s.Cases.Sum(c => References(c.Body, name)),
            Statement.Return r => r.Value is null ? 0 : References(r.Value, name),
            _ => 0
        };
    }
}

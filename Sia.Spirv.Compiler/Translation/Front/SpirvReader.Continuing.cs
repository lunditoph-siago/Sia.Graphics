using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private Expression? NormalizeContinuing(Block tail, string name)
        {
            var place = new Expression.Reference(name, new ShaderType.Pointer(ShaderType.Bool, AddressSpace.Function));
            bool Rewrite(Block block)
            {
                bool found = false;
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    var statement = block.Statements[i];
                    bool exits = statement switch
                    {
                        Statement.Break => true,
                        Statement.If branch => Rewrite(branch.Accept) | Rewrite(branch.Reject),
                        Statement.Nested nested => Rewrite(nested.Body),
                        _ => false
                    };
                    if (!exits) continue;
                    if (i != block.Statements.Count - 1) throw Error("Continuing exits before its last structured statement require control-flow legalization.");
                    if (statement is Statement.Break) block.Statements[i] = new Statement.Store(place, Expression.Bool(true));
                    found = true;
                }
                return found;
            }
            if (!Rewrite(tail)) return null;
            tail.Statements.Insert(0, new Statement.Declare(name, ShaderType.Bool, Expression.Bool(false)));
            return new Expression.Load(place);
        }
    }
}

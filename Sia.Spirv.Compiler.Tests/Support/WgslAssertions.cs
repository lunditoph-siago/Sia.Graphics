using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class WgslAssertions
{
    // Inspect declarations rather than generated names or line formatting.
    internal static void NoMutableLocals(string source)
    {
        var module = WgslReader.Parse(source);
        foreach (var function in module.Functions) Visit(function.Body, function.Name);

        static void Visit(Block block, string function)
        {
            foreach (var statement in block.Statements) {
                if (statement is Statement.Declare declaration) {
                    Assert.False(declaration.Mutable, $"Unexpected mutable local '{function}.{declaration.Name}'.");
                }
                IEnumerable<Block> children = statement switch {
                    Statement.Nested nested => [nested.Body],
                    Statement.If conditional => [conditional.Accept, conditional.Reject],
                    Statement.Loop loop => [loop.Body, loop.Continuing],
                    Statement.Switch selection => selection.Cases.Select(c => c.Body),
                    _ => []
                };
                foreach (var child in children) Visit(child, function);
            }
        }
    }
}

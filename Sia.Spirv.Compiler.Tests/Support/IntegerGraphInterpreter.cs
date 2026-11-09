using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class IntegerGraphInterpreter
{
    // A small strict interpreter checks the lowered graph, including helper
    // calls. Invalid raw SPIR-V shifts/divides throw instead of inheriting the
    // host's masked shift behavior. Test cases supply independent expected values.
    internal static object Evaluate(CanonicalModule module, string function, object[] arguments)
    {
        var graph = module.Functions[function];
        var symbols = graph.Signature.Arguments.Select((a, i) => (a.Name, Value: arguments[i])).ToDictionary(p => p.Name, p => p.Value);
        var values = new Dictionary<int, object>();
        object Read(SsaValue value) => values[value.Id];
        int current = graph.Entry;
        for (int step = 0; step < 1000; step++) {
            var block = graph.Blocks.Single(b => b.Id == current);
            foreach (var instruction in block.Instructions) {
                var result = instruction.Result!.Value;
                object value = instruction.Operation switch {
                    ValueOperation.Literal literal => literal.Value,
                    ValueOperation.Symbol symbol => symbols[symbol.Name],
                    ValueOperation.Let let => Read(let.Value),
                    ValueOperation.Convert c when result.Type == ShaderType.U32 => unchecked((uint)(int)Read(c.Operand)),
                    ValueOperation.Convert c when result.Type == ShaderType.I32 => unchecked((int)(uint)Read(c.Operand)),
                    ValueOperation.Unary { Operator: "-" } u => unchecked(-(int)Read(u.Operand)),
                    ValueOperation.Select s => (bool)Read(s.Condition) ? Read(s.Accept) : Read(s.Reject),
                    ValueOperation.Binary b => Binary(b.Operator, Read(b.Left), Read(b.Right)),
                    ValueOperation.Call call => Evaluate(module, call.Function, call.Arguments.Select(Read).ToArray()),
                    _ => throw new InvalidOperationException("Unexpected operation in arithmetic fixture: " + instruction.Operation)
                };
                values[result.Id] = value;
            }
            if (block.Terminator is ControlFlowTerminator.Return { Value: { } returned }) return Read(returned);
            if (block.Terminator is ControlFlowTerminator.Branch branch) { current = branch.Edge.Target; continue; }
            throw new InvalidOperationException("Unexpected control flow in arithmetic fixture.");
        }
        throw new InvalidOperationException("Arithmetic fixture did not terminate.");
    }

    private static object Binary(string op, object left, object right)
    {
        if (op == "==") return Equals(left, right);
        if (op is "<<" or ">>") {
            uint count = (uint)right;
            if (count >= 32) throw new InvalidOperationException("Out-of-range raw SPIR-V shift.");
            return left is int signed ? (object)(op == "<<" ? signed << (int)count : signed >> (int)count)
                : op == "<<" ? (uint)left << (int)count : (uint)left >> (int)count;
        }
        if (left is uint a && right is uint b) return op switch {
            "/" => a / b, "%" => a % b, "&" => a & b, "|" => a | b, "*" => unchecked(a * b), "-" => unchecked(a - b),
            _ => throw new InvalidOperationException("Unexpected unsigned operation.")
        };
        if (left is int x && right is int y) return op switch {
            "/" => x / y, "%" => x % y, "|" => x | y, "*" => unchecked(x * y), "-" => unchecked(x - y),
            _ => throw new InvalidOperationException("Unexpected signed operation.")
        };
        throw new InvalidOperationException("Unexpected arithmetic operands.");
    }
}

using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;

namespace Sia.Spirv.Compiler.Tests;

public sealed class CanonicalIntegerSemanticsTests
{
    [Theory]
    [InlineData("x / y", int.MinValue, -1, int.MinValue)]
    [InlineData("x % y", int.MinValue, -1, 0)]
    [InlineData("x / y", -7, 0, -7)]
    [InlineData("x % y", -7, 0, 0)]
    [InlineData("x / 3i", int.MinValue, 0, -715827882)]
    [InlineData("x % 3i", int.MinValue, 0, -2)]
    [InlineData("x % -3i", -7, 0, -1)]
    [InlineData("x % -3i", 7, 0, 1)]
    [InlineData("x << y", 1, 32, 1)]
    [InlineData("x << y", 1, 33, 2)]
    [InlineData("x >> y", int.MinValue, 63, -1)]
    [InlineData("x >> y", int.MinValue, -1, -1)]
    public void SignedBoundaryResultsMatchWgsl(string expression, int x, int y, int expected)
    {
        bool shift = expression.Contains("<<", StringComparison.Ordinal) || expression.Contains(">>", StringComparison.Ordinal);
        var canonical = Lower(expression, "i32", shift ? "u32" : "i32");
        object count = shift ? (object)unchecked((uint)y) : y;
        Assert.Equal(expected, Evaluate(canonical, "calculate", [x, count]));
    }

    [Theory]
    [InlineData("x / 8u", 8u, false)]
    [InlineData("x % 8u", 8u, true)]
    [InlineData("x / 2147483648u", 2147483648u, false)]
    [InlineData("x % 2147483648u", 2147483648u, true)]
    [InlineData("x / 3u", 3u, false)]
    [InlineData("x % 3u", 3u, true)]
    public void UnsignedStrengthReductionMatchesDivision(string expression, uint divisor, bool remainder)
    {
        var canonical = Lower(expression, "u32", "u32");
        foreach (uint x in new[] { 0u, 1u, 7u, 8u, 9u, 2147483647u, 2147483648u, uint.MaxValue })
            Assert.Equal(remainder ? x % divisor : x / divisor, Evaluate(canonical, "calculate", [x, 0u]));
    }

    [Fact]
    public void NarrowingToSignedIntegerDoesNotPreserveAnUnsignedRangeProof()
    {
        var module = WgslReader.Parse("""
            enable wgpu_int16;
            fn calculate(y: u32) -> u32 { return u32(i16(y & 65535u)) & 65535u; }
            """);
        var canonical = ShaderTargetLowering.PrepareSpirv(CanonicalShaderPipeline.Prepare(module), null);
        Assert.Equal(2, canonical.Functions["calculate"].Blocks.SelectMany(b => b.Instructions)
            .Count(i => i.Operation is ValueOperation.Binary { Operator: "&" }));
    }

    private static CanonicalModule Lower(string expression, string type, string countType)
    {
        var module = WgslReader.Parse($"fn calculate(x: {type}, y: {countType}) -> {type} {{ return {expression}; }}");
        return ShaderTargetLowering.PrepareSpirv(CanonicalShaderPipeline.Prepare(module), null);
    }

    // A small strict interpreter checks the lowered graph, including helper
    // calls. Invalid raw SPIR-V shifts/divides throw instead of inheriting the
    // host's masked shift behavior. Expected values above are independent oracles.
    private static object Evaluate(CanonicalModule module, string function, object[] arguments)
    {
        var graph = module.Functions[function];
        var symbols = graph.Signature.Arguments.Select((a, i) => (a.Name, Value: arguments[i])).ToDictionary(p => p.Name, p => p.Value);
        var values = new Dictionary<int, object>();
        object Read(SsaValue value) => values[value.Id];
        int current = graph.Entry;
        while (true) {
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
            "/" => a / b, "%" => a % b, "&" => a & b, "*" => unchecked(a * b), "-" => unchecked(a - b),
            _ => throw new InvalidOperationException("Unexpected unsigned operation.")
        };
        if (left is int x && right is int y) return op switch {
            "/" => x / y, "%" => x % y, "*" => unchecked(x * y), "-" => unchecked(x - y),
            _ => throw new InvalidOperationException("Unexpected signed operation.")
        };
        throw new InvalidOperationException("Unexpected arithmetic operands.");
    }
}

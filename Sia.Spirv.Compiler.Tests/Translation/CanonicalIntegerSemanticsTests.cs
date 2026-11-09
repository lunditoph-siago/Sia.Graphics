using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class CanonicalIntegerSemanticsTests
{
    [Theory]
    [InlineData("x / (y | 1u)", false)]
    [InlineData("x % (y | 1u)", true)]
    public void NonzeroUnsignedDivisorsMatchBoundaryResults(string expression, bool remainder)
    {
        var canonical = Lower(expression, "u32", "u32");
        foreach (uint x in new[] { 0u, 1u, 7u, 8u, 0x80000000u, uint.MaxValue })
            foreach (uint y in new[] { 0u, 1u, 2u, 7u, 0x80000000u, uint.MaxValue })
                Assert.Equal(remainder ? x % (y | 1u) : x / (y | 1u), IntegerGraphInterpreter.Evaluate(canonical, "calculate", [x, y]));
    }

    [Theory]
    [InlineData("x / (y | 1i)", int.MinValue)]
    [InlineData("x % (y | 1i)", 0)]
    public void NonzeroSignedDivisorsStillHandleOverflow(string expression, int expected)
        => Assert.Equal(expected, IntegerGraphInterpreter.Evaluate(Lower(expression, "i32", "i32"), "calculate", [int.MinValue, -1]));

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
        Assert.Equal(expected, IntegerGraphInterpreter.Evaluate(canonical, "calculate", [x, count]));
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
            Assert.Equal(remainder ? x % divisor : x / divisor, IntegerGraphInterpreter.Evaluate(canonical, "calculate", [x, 0u]));
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

}

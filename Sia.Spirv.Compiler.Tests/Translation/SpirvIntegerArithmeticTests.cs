using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvIntegerArithmeticTests
{
    [Theory]
    [InlineData("x << y", "u32", (int)Op.ShiftLeftLogical)]
    [InlineData("x >> y", "u32", (int)Op.ShiftRightLogical)]
    [InlineData("x >> y", "i32", (int)Op.ShiftRightArithmetic)]
    [InlineData("x << y", "vec3<u32>", (int)Op.ShiftLeftLogical)]
    [InlineData("x >> y", "vec3<i32>", (int)Op.ShiftRightArithmetic)]
    public void RuntimeShiftsMaskTheCount(string expression, string type, int operation)
    {
        var binary = IntegerTestShader.Compile(expression, type);
        var shift = Assert.Single(binary.Instructions, i => i.Opcode == (ushort)operation);
        var mask = Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.BitwiseAnd);
        Assert.Equal(mask.Operands[1], shift.Operands[3]);
    }

    [Theory]
    [InlineData("x / 3u", (int)Op.UDiv)]
    [InlineData("x % 3u", (int)Op.UMod)]
    [InlineData("x / 8u", (int)Op.ShiftRightLogical)]
    [InlineData("x % 8u", (int)Op.BitwiseAnd)]
    public void SafeUnsignedConstantsAvoidCheckedHelpers(string expression, int operation)
    {
        var binary = IntegerTestShader.Compile(expression);
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.FunctionCall); // Entry wrapper only.
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)operation);
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode == (ushort)Op.IEqual);
    }

    [Fact]
    public void SignedRemainderWithSafeConstantUsesPortableInlineArithmetic()
    {
        var binary = IntegerTestShader.Compile("x % 3i", "i32");
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.FunctionCall);
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode is (ushort)Op.SRem or (ushort)Op.SMod);
        foreach (var op in new[] { Op.SDiv, Op.IMul, Op.ISub })
            Assert.Single(binary.Instructions, i => i.Opcode == (ushort)op);
    }

    [Theory]
    [InlineData("x / y", "u32")]
    [InlineData("x % y", "i32")]
    [InlineData("x / -1i", "i32")]
    public void PotentiallyInvalidDivisorsKeepChecks(string expression, string type)
    {
        var binary = IntegerTestShader.Compile(expression, type);
        Assert.Equal(2, binary.Instructions.Count(i => i.Opcode == (ushort)Op.FunctionCall));
        Assert.Contains(binary.Instructions, i => i.Opcode == (ushort)Op.Select);
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode is (ushort)Op.SRem or (ushort)Op.SMod);
    }

    [Fact]
    public void ConstantZeroDivisorRemainsAShaderCreationError()
        => Assert.Throws<ShaderException>(() => IntegerTestShader.Compile("x / 0u"));

    [Theory]
    [InlineData("x * data[1].x")]
    [InlineData("data[1].x * x")]
    public void FloatVectorScalarMultiplyUsesNativeInstruction(string expression)
    {
        var binary = IntegerTestShader.Compile(expression, "vec3<f32>");
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.VectorTimesScalar);
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode is (ushort)Op.FMul or (ushort)Op.CompositeConstruct);
    }

    [Theory]
    [InlineData("x / vec3<u32>(2u, 8u, 16u)", "vec3<u32>", (int)Op.ShiftRightLogical)]
    [InlineData("x % vec3<u32>(2u, 8u, 16u)", "vec3<u32>", (int)Op.BitwiseAnd)]
    [InlineData("x / 3u", "vec3<u32>", (int)Op.UDiv)]
    [InlineData("x / 3i", "vec3<i32>", (int)Op.SDiv)]
    public void ConstantDivisionHandlesEachVectorLane(string expression, string type, int operation)
    {
        var binary = IntegerTestShader.Compile(expression, type);
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.FunctionCall);
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)operation);
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode == (ushort)Op.Select);
    }

    [Theory]
    [InlineData("x << 3u", 0)]
    [InlineData("x << (y & 31u)", 1)]
    [InlineData("x << (31u & y)", 1)]
    [InlineData("x << (y & 15u)", 1)]
    [InlineData("x << (y & 63u)", 2)]
    public void ShiftMaskIsOmittedOnlyWhenTheCountIsProvenBounded(string expression, int masks)
        => Assert.Equal(masks, IntegerTestShader.Compile(expression).Instructions.Count(i => i.Opcode == (ushort)Op.BitwiseAnd));

    [Theory]
    [InlineData("x << 32u", "u32")]
    [InlineData("x >> 33u", "i32")]
    [InlineData("x << vec3<u32>(0u, 31u, 32u)", "vec3<u32>")]
    public void OutOfRangeConstantShiftCountIsAShaderCreationError(string expression, string type)
    {
        var error = Assert.Throws<ShaderException>(() => IntegerTestShader.Compile(expression, type));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("shift count", error.Diagnostic.Message);
    }

    [Fact]
    public void RuntimeLetShiftCountStillUsesModuloSemantics()
    {
        string source = IntegerTestShader.Source("x << y").Replace("let y = u32(data[1]);", "let y = 32u;", StringComparison.Ordinal);
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.ShiftLeftLogical);
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.BitwiseAnd);
    }

    [Fact]
    public void UnresolvedOverrideDefaultDoesNotProveTheDivisorSafe()
    {
        string source = "override divisor: u32 = 8u;\n" + IntegerTestShader.Source("x / divisor");
        var unresolved = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Equal(2, unresolved.Instructions.Count(i => i.Opcode == (ushort)Op.FunctionCall));
        var resolved = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default,
            new() { PipelineConstants = new Dictionary<string, double> { ["divisor"] = 8 } }));
        Assert.Single(resolved.Instructions, i => i.Opcode == (ushort)Op.FunctionCall);
        Assert.Single(resolved.Instructions, i => i.Opcode == (ushort)Op.ShiftRightLogical);
    }

    [Fact]
    public void SignedRemainderDoesNotRepeatEffectfulNumerator()
    {
        const string source = """
            @group(0) @binding(0) var<storage, read_write> data: array<atomic<i32>>;
            @compute @workgroup_size(1) fn main() {
                atomicStore(&data[1], atomicAdd(&data[0], 1i) % 3i);
            }
            """;
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.AtomicIAdd);
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.FunctionCall);
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.SDiv);
    }
}

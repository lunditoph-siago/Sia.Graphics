using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvIntegerArithmeticTests
{
    [Theory]
    [InlineData("x << y", "u32", (int)Op.ShiftLeftLogical)]
    [InlineData("x >> y", "i32", (int)Op.ShiftRightArithmetic)]
    [InlineData("x >> y", "vec3<i32>", (int)Op.ShiftRightArithmetic)]
    public void RuntimeShiftsMaskTheCount(string expression, string type, int operation)
    {
        var binary = IntegerTestShader.Compile(expression, type);
        var shift = Assert.Single(binary.Instructions, i => i.Opcode == (ushort)operation);
        var mask = Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.BitwiseAnd);
        Assert.Equal(mask.Operands[1], shift.Operands[3]);
    }

    [Fact]
    public void ConstantZeroDivisorRemainsAShaderCreationError()
        => Assert.Throws<ShaderException>(() => IntegerTestShader.Compile("x / 0u"));

    [Theory]
    [InlineData("x << 32u", "u32")]
    [InlineData("x << vec3<u32>(0u, 31u, 32u)", "vec3<u32>")]
    public void OutOfRangeConstantShiftCountIsAShaderCreationError(string expression, string type)
    {
        var error = Assert.Throws<ShaderException>(() => IntegerTestShader.Compile(expression, type));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("shift count", error.Diagnostic.Message);
    }

    [Fact]
    public void UnresolvedOverrideDefaultDoesNotProveTheDivisorSafe()
    {
        string source = "override divisor: u32 = 8u;\n" + IntegerTestShader.Source("x / divisor");
        var unresolved = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Equal(2, unresolved.Instructions.Count(i => i.Opcode == (ushort)Op.FunctionCall));
        Assert.Contains(unresolved.Instructions, i => i.Opcode == (ushort)Op.SpecConstant);
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
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.SDiv);
    }
}

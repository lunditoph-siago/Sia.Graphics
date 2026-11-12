using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvNonzeroDivisorTests
{
    [Theory]
    [InlineData("x / (y | vec3<u32>(1u, 0u, 2u))", "vec3<u32>")]
    [InlineData("x / (y | 1i)", "i32")]
    public void ZeroLanesAndSignedOverflowKeepSafetyHelpers(string expression, string type)
    {
        var binary = IntegerTestShader.Compile(expression, type);
        Assert.Equal(2, binary.Instructions.Count(i => i.Opcode == (ushort)Op.FunctionCall));
        Assert.Contains(binary.Instructions, i => i.Opcode == (ushort)Op.Select);
    }

    [Fact]
    public void NonzeroProofPreservesAtomicOperandEvaluation()
    {
        const string source = """
            @group(0) @binding(0) var<storage, read_write> data: array<atomic<u32>>;
            @compute @workgroup_size(1) fn main() {
                atomicStore(&data[1], atomicAdd(&data[0], 1u) / (atomicAdd(&data[0], 1u) | 1u));
            }
            """;
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Equal(2, binary.Instructions.Count(i => i.Opcode == (ushort)Op.AtomicIAdd));
        string wgsl = ShaderTranslator.SpirvToWgsl(binary.ToBytes(), SpirvCompilationTarget.Default);
        Assert.Equal(new uint[] { 9, 0 }, WgslFixtureInterpreter.Execute(wgsl).Memory);
    }

    [Fact]
    public void NarrowingCannotKeepAProofFromDiscardedHighBits()
    {
        var module = WgslReader.Parse("""
            enable wgpu_int16;
            fn calculate(x: u32, y: u32) -> u32 { return x / u32(u16(y | 65536u)); }
            """);
        var canonical = SpirvBackend.PrepareCanonical(CanonicalShaderPipeline.Prepare(module), null);
        Assert.Single(canonical.Functions["calculate"].Blocks.SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Call);
    }

    [Fact]
    public void AZeroIncomingBranchCannotInheritANonzeroProof()
    {
        string source = IntegerTestShader.Source("x / divisor")
            .Replace("data[2] =", "var divisor = 1u; if y == 0u { divisor = 0u; } data[2] =", StringComparison.Ordinal);
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Equal(2, binary.Instructions.Count(i => i.Opcode == (ushort)Op.FunctionCall));
    }

}

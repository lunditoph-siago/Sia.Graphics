using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvValueOptimizationTests
{
    internal const string ConstantVectorProduct = """
        @group(0) @binding(0) var<storage, read_write> data: array<u32>;
        fn pick(v: vec4<u32>) -> u32 { return v[3u]; }
        @compute @workgroup_size(1) fn main() {
            let x = pick(vec4<u32>(10u, 11u, 12u, data[0]));
            data[1] = x * x + x * x;
        }
        """;

    [Fact]
    public void WgslReusesTheProductAndExtractsTheConstantLane()
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(ConstantVectorProduct, SpirvCompilationTarget.Default);
        var instructions = SpirvBinary.Parse(binary).Instructions;
        Assert.Single(instructions, i => i.Opcode == (ushort)Op.IMul);
        Assert.Single(instructions, i => i.Opcode == (ushort)Op.CompositeExtract);
        Assert.DoesNotContain(instructions, i => i.Opcode == (ushort)Op.VectorExtractDynamic);
        AssertMemory(binary, 7u, 98u);
    }

    [Fact]
    public void ReusesRepeatedIlProduct()
    {
        byte[] binary = SpirvTestAssembly.CompileRuntime(typeof(MemorySemanticsShaders), nameof(MemorySemanticsShaders.RepeatedProduct));
        Assert.Single(SpirvBinary.Parse(binary).Instructions, i => i.Opcode == (ushort)Op.IMul);
        AssertMemory(binary, 7u, 98u);
    }

    [Fact]
    public void DynamicVectorIndexRemainsDynamic()
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(Compute(
            "data[1] = pick(vec4<u32>(data[0], 11u, 12u, 13u), data[0] & 3u);",
            "fn pick(v: vec4<u32>, i: u32) -> u32 { return v[i]; }"), SpirvCompilationTarget.Default);
        Assert.Single(SpirvBinary.Parse(binary).Instructions, i => i.Opcode == (ushort)Op.VectorExtractDynamic);
        AssertMemory(binary, 7u, 13u);
    }

    [SpirvToolchainFact]
    [Trait("Category", "Toolchain")]
    public void NegativeRuntimeVectorIndexRemainsDynamic()
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(Compute(
            "data[1] = pick(vec4<u32>(data[0], 11u, 12u, 13u));",
            "fn pick(v: vec4<u32>) -> u32 { let i = -1; return v[i]; }"), SpirvCompilationTarget.Default);
        Assert.Single(SpirvBinary.Parse(binary).Instructions, i => i.Opcode == (ushort)Op.VectorExtractDynamic);
        SpirvTestToolchain.Validate(binary);
    }

    [Fact]
    public void RepeatedReadsAcrossAWriteRemainDistinct()
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(Compute("""
            let a = data[0] * data[0]; data[0] += 1u;
            let b = data[0] * data[0]; data[1] = a + b;
            """), SpirvCompilationTarget.Default);
        var instructions = SpirvBinary.Parse(binary).Instructions;
        Assert.Equal(2, instructions.Count(i => i.Opcode == (ushort)Op.IMul));
        Assert.Equal(5, instructions.Count(i => i.Opcode == (ushort)Op.Load));
        AssertMemory(binary, 8u, 113u);
    }

    [Theory]
    [InlineData("/", 7u)]
    [InlineData("%", 0u)]
    public void ConvertedRuntimeZeroDivisorsDoNotBecomeConstantStageErrors(string operation, uint expected)
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(Compute($$"""
            let x = 0u; let unused = i32(0u);
            data[1] = data[0] {{operation}} u32(i32(x));
            """), SpirvCompilationTarget.Default);
        AssertMemory(binary, 7u, expected);
    }

    private static string Compute(string body, string helper = "") => $$"""
        @group(0) @binding(0) var<storage, read_write> data: array<u32>;
        {{helper}}
        @compute @workgroup_size(1) fn main() { {{body}} }
        """;

    private static void AssertMemory(byte[] binary, params uint[] expected)
    {
        string wgsl = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        Assert.Equal(expected, WgslFixtureInterpreter.Execute(wgsl).Memory);
    }
}

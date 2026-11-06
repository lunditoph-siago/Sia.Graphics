using Sia.Spirv;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.LLVM;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvQualityRegressionTests
{
    [SpirvToolchainFact]
    public void TextureOffsetsAndRuntimeNegativeIndicesProduceValidSpirv()
    {
        string[] sources = [
            """
            @group(0) @binding(0) var image: texture_2d<f32>;
            @group(0) @binding(1) var filtering: sampler;
            @fragment fn main() -> @location(0) vec4<f32> {
                return textureSample(image, filtering, vec2<f32>(), vec2<i32>(1, 2) + vec2<i32>(3, 4));
            }
            """,
            """
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() { let index = -1; data[1] = data[index]; }
            """
        ];
        string directory = Path.Combine(Path.GetTempPath(), "sia-spirv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var toolchain = new LlvmToolchain(SpirvTestToolchain.Directory!);
            foreach (string source in sources) {
                string path = Path.Combine(directory, "shader.spv");
                File.WriteAllBytes(path, ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
                toolchain.Validate(path, "vulkan1.2");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void UnusedPureWorkDoesNotReachTheSpirvBackend()
    {
        const string source = """
            @group(0) @binding(0) var<storage, read> data: array<f32>;
            @compute @workgroup_size(1) fn main() { let unused = sin(data[0]) * 2f; }
            """;
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode is (ushort)Op.ExtInst or (ushort)Op.FMul);
    }

    [Fact]
    public void InvalidSourceIsRejectedBeforeDeadValueElimination()
        => Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(
            "@compute @workgroup_size(1) fn main() { let unused = vec2<u32>(1u)[3]; }", SpirvCompilationTarget.Default));
}

using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvOutputValidationTests
{
    [SpirvToolchainTheory]
    [Trait("Category", "Toolchain")]
    [InlineData("x << y", "u32")]
    [InlineData("x >> y", "vec3<i32>")]
    [InlineData("x << (y & 31u)", "u32")]
    [InlineData("x / 3u", "u32")]
    [InlineData("x % 8u", "u32")]
    [InlineData("x % 3i", "i32")]
    [InlineData("x / vec3<u32>(2u, 8u, 16u)", "vec3<u32>")]
    [InlineData("x % vec3<i32>(3i, -2i, 5i)", "vec3<i32>")]
    [InlineData("x / y", "i32")]
    [InlineData("x / -1i", "i32")]
    [InlineData("x * data[1].x", "vec3<f32>")]
    public void ArithmeticOutputsPassSpirvValidation(string expression, string type)
        => SpirvTestToolchain.Validate(IntegerTestShader.Compile(expression, type).ToBytes());

    public static TheoryData<string, string> IndexAndOffsetCases => new() {
        { "folded texture offset", """
            @group(0) @binding(0) var image: texture_2d<f32>;
            @group(0) @binding(1) var filtering: sampler;
            @fragment fn main() -> @location(0) vec4<f32> {
                return textureSample(image, filtering, vec2<f32>(), vec2<i32>(1, 2) + vec2<i32>(3, 4));
            }
            """ },
        { "runtime negative index", """
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() { let index = -1; data[1] = data[index]; }
            """ }
    };

    [SpirvToolchainTheory]
    [Trait("Category", "Toolchain")]
    [MemberData(nameof(IndexAndOffsetCases))]
    public void IndexAndOffsetOutputsPassSpirvValidation(string scenario, string source)
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        Assert.True(binary.Length > 0, scenario);
        SpirvTestToolchain.Validate(binary);
    }
}

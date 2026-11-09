using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvOutputValidationTests
{
    public static TheoryData<string, string> CoreSources => new() {
        { "unsigned arithmetic", Compute("let x = data[0]; data[1] = (x << x) / (x | 1u);") },
        { "signed vector remainder", Compute("data[1] = data[0] % vec3<i32>(3i, -2i, 5i);", "vec3<i32>") },
        { "runtime negative index", Compute("let i = -1; data[1] = data[i];") },
        { "runtime vector index", Compute("let i = 99; data[1] = vec4<u32>(1u)[i];") },
        { "runtime vector insert", Compute("var v = vec2<u32>(1u); let i = 3; v[i] = data[0]; data[1] = v.x;") },
        { "runtime array index", Compute("let i = 3; data[1] = array<u32, 2>(1u, 2u)[i];") },
        { "runtime matrix index", Compute("let i = 4; data[1] = u32(mat2x2<f32>(1f, 2f, 3f, 4f)[i].x);") },
        { "constant texture offset", """
            @group(0) @binding(0) var image: texture_2d<f32>;
            @group(0) @binding(1) var filtering: sampler;
            @fragment fn main() -> @location(0) vec4<f32> {
                return textureSample(image, filtering, vec2<f32>(), vec2<i32>(1, 2) + vec2<i32>(3, 4));
            }
            """ }
    };

    [SpirvToolchainTheory]
    [Trait("Category", "Toolchain")]
    [MemberData(nameof(CoreSources))]
    public void CoreSourcesProduceValidSpirvAndRoundtrip(string scenario, string source)
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        Assert.True(binary.Length > 0, scenario);
        SpirvTestToolchain.Validate(binary);
        string wgsl = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        SpirvTestToolchain.Validate(ShaderTranslator.WgslToSpirv(wgsl, SpirvCompilationTarget.Default));
    }

    private static string Compute(string body, string type = "u32") => $$"""
        @group(0) @binding(0) var<storage, read_write> data: array<{{type}}>;
        @compute @workgroup_size(1) fn main() { {{body}} }
        """;
}

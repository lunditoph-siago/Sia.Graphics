using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvRuntimeIndexTests
{
    [Theory]
    [InlineData("let i = 99; data[1] = vec4<u32>(1u)[i];")]
    [InlineData("var v = vec2<u32>(1u); let i = 3; v[i] = data[0]; data[1] = v.x;")]
    [InlineData("let i = 4; data[1] = u32(mat2x2<f32>(1f, 2f, 3f, 4f)[i].x);")]
    [InlineData("let i = 3; data[1] = array<u32, 2>(1u, 2u)[i];")]
    [InlineData("let i = -1; data[1] = data[i];")]
    public void NativeDynamicIndicesKeepTheirRuntimeEvaluationStage(string body)
    {
        string source = $$"""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() { {{body}} }
            """;
        var binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        string result = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        WgslReader.Parse(result);
    }
}

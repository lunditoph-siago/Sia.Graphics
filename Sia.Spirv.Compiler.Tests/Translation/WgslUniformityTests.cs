using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class WgslUniformityTests
{
    [Fact]
    public void CollectiveReadRemainsUniformAfterNativeRoundtrip()
    {
        string source = $$"""
            var<workgroup> data: array<i32, 128>;
            @compute @workgroup_size(4) fn main(@builtin(workgroup_id) id: vec3<u32>) {
                let value = workgroupUniformLoad(&data[id.x]);
                if value > 10i { workgroupBarrier(); }
            }
            """;
        var binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void SourcePrivateCopiesStillRejectCollectiveReads()
    {
        var error = Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($$"""
            var<private> copy: vec3<u32>;
            var<workgroup> data: array<i32, 128>;
            @compute @workgroup_size(4) fn main(@builtin(workgroup_id) id: vec3<u32>) {
                copy = id;
                let value = workgroupUniformLoad(&data[copy.x]);
                if value > 10i { workgroupBarrier(); }
            }
            """, SpirvCompilationTarget.Default));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("Uniformity violation", error.Diagnostic.Message);
    }

    [Fact]
    public void DerivativeEvaluationStaysOutsideDivergentControlFlow()
    {
        string result = ShaderTranslation.NormalizeWgsl("""
            @fragment fn main(@location(0) value: f32) -> @location(0) f32 {
                let derivative = dpdx(value);
                if (value > 0f) { return derivative; }
                return 0f;
            }
            """);
        Assert.True(result.IndexOf("dpdx(", StringComparison.Ordinal) < result.IndexOf("if (", StringComparison.Ordinal), result);
        WgslReader.Parse(result);
    }
}

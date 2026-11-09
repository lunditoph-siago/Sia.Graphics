using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class WgslUniformityTests
{
    [Theory]
    [InlineData("0u")]
    [InlineData("id.x")]
    public void CollectiveReadRemainsUniformAfterNativeRoundtrip(string index)
    {
        string source = $$"""
            var<workgroup> data: array<i32, 128>;
            @compute @workgroup_size(4) fn main(@builtin(workgroup_id) id: vec3<u32>) {
                let value = workgroupUniformLoad(&data[{{index}}]);
                if value > 10i { workgroupBarrier(); }
            }
            """;
        var binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData("copy = id;")]
    [InlineData("copy = local;")]
    [InlineData("copy = id; if local.x == 0u { copy = vec3<u32>(1u); }")]
    [InlineData("copy = id; copy.x = local.x;")]
    [InlineData("copy = id; mutate(local);")]
    public void SourcePrivateCopiesStillRejectCollectiveReads(string setup)
    {
        string helper = setup.Contains("mutate", StringComparison.Ordinal) ? "fn mutate(value: vec3<u32>) { copy = value; }" : "";
        var error = Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($$"""
            var<private> copy: vec3<u32>;
            var<workgroup> data: array<i32, 128>;
            {{helper}}
            @compute @workgroup_size(4) fn main(@builtin(workgroup_id) id: vec3<u32>, @builtin(local_invocation_id) local: vec3<u32>) {
                {{setup}}
                let value = workgroupUniformLoad(&data[copy.x]);
                if value > 10i { workgroupBarrier(); }
            }
            """, SpirvCompilationTarget.Default));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("Uniformity violation", error.Diagnostic.Message);
    }
}

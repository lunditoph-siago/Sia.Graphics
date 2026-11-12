using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvToWgslTests
{
    private const string StraightLineBody = "data[1] = data[0] + 1u;";

    [Fact]
    public void StraightLineStorageShaderUsesCanonicalHandoff()
    {
        var target = SpirvCompilationTarget.Default;
        var binary = Compile(StraightLineBody);

        Assert.IsType<SpirvReader.WgslInput.Canonical>(SpirvReader.ReadForWgsl(binary, null));
        var wgsl = ShaderTranslator.SpirvToWgsl(binary, target);
        Assert.Equal(WgslWriter.Write(SpirvReader.Parse(binary), target), wgsl);
        Assert.Equal(new uint[] { 7, 8 }, WgslFixtureInterpreter.Execute(wgsl).Memory);
    }

    [Theory]
    [InlineData("if data[0] != 0u { data[1] = 3u; }", "")]
    [InlineData("workgroupBarrier(); data[1] = data[0];", "")]
    [InlineData("var v = vec2<u32>(data[0], 2u); data[1] = first(&v);",
        "fn first(p: ptr<function, vec2<u32>>) -> u32 { return (*p)[0]; }")]
    public void StructuredFeaturesKeepTheExistingAdapter(string body, string helper)
    {
        var target = SpirvCompilationTarget.Default;
        var binary = Compile(body, helper);
        var options = new SpirvReadOptions(AdjustCoordinateSpace: false);

        Assert.IsType<SpirvReader.WgslInput.Structured>(SpirvReader.ReadForWgsl(binary, options));
        Assert.Equal(WgslWriter.Write(SpirvReader.Parse(binary, options), target),
            ShaderTranslator.SpirvToWgsl(binary, target, options));
    }

    [Fact]
    public void MatrixStorageKeepsTheStructuredAdapter()
    {
        var target = SpirvCompilationTarget.Default;
        var binary = ShaderTranslator.WgslToSpirv("""
            struct Data { matrix: mat2x2<f32> }
            @group(0) @binding(0) var<storage, read> input: Data;
            @group(0) @binding(1) var<storage, read_write> output: array<f32>;
            @compute @workgroup_size(1) fn main() { output[0] = input.matrix[1].x; }
            """, target);

        Assert.IsType<SpirvReader.WgslInput.Structured>(SpirvReader.ReadForWgsl(binary, null));
        Assert.Equal(WgslWriter.Write(SpirvReader.Parse(binary), target), ShaderTranslator.SpirvToWgsl(binary, target));
    }

    [Fact]
    public void CanonicalHandoffPreservesTargetResourceDiagnostic()
    {
        var binary = Compile(StraightLineBody);
        var target = SpirvCompilationTarget.Default with {
            ResourceLimits = SpirvTargetProfile.Default with { SupportsStorageBuffers = false }
        };

        var expected = Assert.Throws<ShaderException>(() => WgslWriter.Write(SpirvReader.Parse(binary), target));
        var actual = Assert.Throws<ShaderException>(() => ShaderTranslator.SpirvToWgsl(binary, target));
        Assert.Equal(expected.Diagnostic, actual.Diagnostic);
    }

    private static byte[] Compile(string body, string helper = "")
        => ShaderTranslator.WgslToSpirv($$"""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            {{helper}}
            @compute @workgroup_size(1) fn main() { {{body}} }
            """, SpirvCompilationTarget.Default);
}

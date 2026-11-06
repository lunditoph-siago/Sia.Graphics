using System.Diagnostics;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests;

public sealed class WgslUpstreamRegressionTests
{
    [UpstreamCompilerFact]
    public void NativeTintAcceptsUniformBuiltinProxies()
    {
        string[] sources = ["""
            var<workgroup> data: array<i32, 128>;
            fn read(index: u32) { if workgroupUniformLoad(&data[index]) > 10i { workgroupBarrier(); } }
            @compute @workgroup_size(4) fn main(@builtin(workgroup_id) id: vec3<u32>) { read(id.x); }
            """, """
            enable subgroups;
            @compute @workgroup_size(64) fn main(@builtin(subgroup_size) size: u32) {
                _ = subgroupShuffleXor(1u, size - 1u);
            }
            """];
        string directory = Path.Combine(Path.GetTempPath(), "sia-spirv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            foreach (string source in sources) {
                var binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
                string input = Path.Combine(directory, "input.wgsl"), output = Path.Combine(directory, "output.wgsl");
                File.WriteAllText(input, ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default));
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("SIA_TEST_TINT")!) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                };
                foreach (string argument in new[] { input, "--format", "wgsl", "-o", output }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
                bool completed = process.WaitForExit(30000);
                if (!completed) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                Assert.True(completed, "Tint timed out.");
                Assert.True(process.ExitCode == 0, stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DiscardedAbstractConstantsDoNotRequireI32Representability()
        => ShaderTranslator.WgslToSpirv("const a = 1 << 30u; const b = a << 10u; const c = 5000000000 << 3u; @compute @workgroup_size(1) fn main() { _ = b; _ = c; }", SpirvCompilationTarget.Default);

    [Fact]
    public void DiscardedInvalidAbstractExpressionsStillReportErrors()
        => Assert.Throws<ShaderException>(() => WgslReader.Parse("@compute @workgroup_size(1) fn main() { _ = 1 / 0; }"));

    [Theory]
    [InlineData("bitcast<u32>(4294967295)", uint.MaxValue)]
    [InlineData("bitcast<vec2<u32>>(vec2(4294967295, 1)).x", uint.MaxValue)]
    [InlineData("bitcast<u32>(bitcast<f32>(1))", 1u)]
    [InlineData("bitcast<u32>(bitcast<i32>(1.0))", 1065353216u)]
    [InlineData("bitcast<u32>(bitcast<vec2<f16>>(vec2(1, 2)))", 1073757184u)]
    [InlineData("bitcast<u32>(vec2<f16>(bitcast<f16>(1), 0h))", 15360u)]
    public void AbstractBitcastOverloadsChooseTheRequiredConcreteType(string expression, uint expected)
    {
        var module = WgslReader.Parse($$"""
            enable f16;
            const bits = {{expression}};
            const_assert bits == {{expected}}u;
            @compute @workgroup_size(1) fn main() { }
            """);
        Assert.Equal(expected, Assert.IsType<Expression.Literal>(module.Constants.Single(c => c.Name == "bits").Value).Value);
    }

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
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($$"""
            var<private> copy: vec3<u32>;
            var<workgroup> data: array<i32, 128>;
            {{helper}}
            @compute @workgroup_size(4) fn main(@builtin(workgroup_id) id: vec3<u32>, @builtin(local_invocation_id) local: vec3<u32>) {
                {{setup}}
                let value = workgroupUniformLoad(&data[copy.x]);
                if value > 10i { workgroupBarrier(); }
            }
            """, SpirvCompilationTarget.Default));
    }
}

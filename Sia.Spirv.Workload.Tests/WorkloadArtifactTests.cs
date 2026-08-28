using System.Text.Json;

namespace Sia.Spirv.Workload.Tests;

public sealed class WorkloadArtifactTests
{
    private static readonly IReadOnlyDictionary<string, ExpectedKernel> s_ExpectedKernels =
        new Dictionary<string, ExpectedKernel>(StringComparer.Ordinal) {
            ["Integrate"] = new(
                "Smoke.Modules.SimulationKernels.Integrate",
                "compute",
                64,
                1,
                1),
            ["ToneMap"] = new(
                "Smoke.Modules.PostProcessKernels.ToneMap",
                "compute",
                128,
                1,
                1),
            ["Classify2D"] = new(
                "Smoke.Modules.PostProcessKernels.Classify2D",
                "compute",
                8,
                4,
                1),
            ["FullscreenVertex"] = new(
                "Smoke.Modules.RasterShaders.FullscreenVertex",
                "vertex",
                1,
                1,
                1),
            ["SolidFragment"] = new(
                "Smoke.Modules.RasterShaders.SolidFragment",
                "fragment",
                1,
                1,
                1)
        };

    private static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "spirv");

    [Fact]
    public void WorkloadEmitsTheCompleteArtifactSet()
    {
        Assert.True(Directory.Exists(OutputDirectory),
            $"The workload output directory '{OutputDirectory}' does not exist.");
        Assert.Equal(s_ExpectedKernels.Count, Directory.GetFiles(OutputDirectory, "*.spv").Length);
        Assert.Equal(s_ExpectedKernels.Count, Directory.GetFiles(OutputDirectory, "*.wgsl").Length);
        Assert.Equal(
            s_ExpectedKernels.Count,
            Directory.GetFiles(OutputDirectory, "*.spv.json").Length);
    }

    [Fact]
    public void WorkloadEmitsWebGpuCompatibleModules()
    {
        foreach (var expected in s_ExpectedKernels.Values) {
            var spirvPath = GetArtifactPath(expected, ".spv");
            var bytes = File.ReadAllBytes(spirvPath);
            Assert.True(bytes.Length >= 4, $"'{spirvPath}' is shorter than a SPIR-V header.");
            Assert.Equal([0x03, 0x02, 0x23, 0x07], bytes[..4]);

            var wgslPath = GetArtifactPath(expected, ".wgsl");
            var wgsl = File.ReadAllText(wgslPath);
            Assert.Contains($"@{expected.Stage}", wgsl);
            Assert.DoesNotContain("var<push_constant>", wgsl);
            if (expected.Stage == "compute") {
                Assert.Contains("@group(0)", wgsl);
            }
        }
    }

    [Fact]
    public void WorkloadEmitsTheManifestContract()
    {
        var entryPoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifestPath in Directory.GetFiles(OutputDirectory, "*.spv.json")) {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var entryPoint = root.GetProperty("entryPoint").GetString();
            Assert.False(string.IsNullOrWhiteSpace(entryPoint));
            Assert.True(
                s_ExpectedKernels.TryGetValue(entryPoint, out var expected),
                $"'{manifestPath}' declares unexpected entry point '{entryPoint}'.");
            Assert.True(entryPoints.Add(entryPoint));

            Assert.Equal("webgpu", root.GetProperty("kernelAbi").GetString());
            Assert.Equal(expected.Stage, root.GetProperty("shaderStage").GetString());

            var workgroupSize = root.GetProperty("workgroupSize");
            Assert.Equal(expected.WorkgroupX, workgroupSize.GetProperty("x").GetUInt32());
            Assert.Equal(expected.WorkgroupY, workgroupSize.GetProperty("y").GetUInt32());
            Assert.Equal(expected.WorkgroupZ, workgroupSize.GetProperty("z").GetUInt32());

            var resources = root.GetProperty("resources").EnumerateArray().ToArray();
            var pushConstants = root.GetProperty("pushConstants").EnumerateArray().ToArray();
            var bindings = resources
                .Select(static resource => resource.GetProperty("binding").GetInt32())
                .ToArray();
            Assert.Equal(bindings.Length, bindings.Distinct().Count());

            if (expected.Stage == "compute") {
                var parameterResource = Assert.Single(resources, static resource =>
                    resource.GetProperty("name").GetString() == "sia.parameters");
                Assert.Equal(2, parameterResource.GetProperty("binding").GetInt32());
                Assert.True(pushConstants.Length >= 2);
            }
            else {
                Assert.Empty(resources);
                Assert.Empty(pushConstants);
            }
        }

        Assert.Equal(
            s_ExpectedKernels.Keys.Order(StringComparer.Ordinal),
            entryPoints.Order(StringComparer.Ordinal));
    }

    private static string GetArtifactPath(ExpectedKernel expected, string extension) =>
        Path.Combine(OutputDirectory, expected.QualifiedName + extension);

    private sealed record ExpectedKernel(
        string QualifiedName,
        string Stage,
        uint WorkgroupX,
        uint WorkgroupY,
        uint WorkgroupZ);
}

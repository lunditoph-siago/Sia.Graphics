using System.Buffers.Binary;
using System.Text.Json;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvCompilerTests
{
    [SpirvToolchainFact]
    public void CompileAssemblyProducesValidatedWebGpuArtifactsAndCacheHits()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "sia-spirv-tests",
            Guid.NewGuid().ToString("N"));
        try {
            var compiler = new SpirvCompiler();
            var options = new SpirvCompilationOptions {
                ToolchainDirectory = SpirvTestToolchain.Directory,
                KernelAbi = SpirvKernelAbi.WebGpu,
                EmitWgsl = true,
                EmitLlvmIr = false
            };

            var artifacts = compiler.CompileAssembly(
                SpirvTestAssembly.Path,
                outputDirectory,
                options);

            Assert.Equal(15, artifacts.Count);
            Assert.All(artifacts, artifact => AssertArtifact(artifact));
            Assert.DoesNotContain(artifacts, artifact => artifact.CacheHit);

            var cachedArtifacts = compiler.CompileAssembly(
                SpirvTestAssembly.Path,
                outputDirectory,
                options);

            Assert.Equal(artifacts.Count, cachedArtifacts.Count);
            Assert.All(cachedArtifacts, artifact => Assert.True(artifact.CacheHit));
        } finally {
            if (System.IO.Directory.Exists(outputDirectory)) {
                System.IO.Directory.Delete(outputDirectory, true);
            }
        }
    }

    private static void AssertArtifact(SpirvArtifact artifact)
    {
        var bytecode = File.ReadAllBytes(artifact.SpirvPath);
        Assert.True(bytecode.Length >= 20);
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(bytecode));

        var wgslPath = Assert.IsType<string>(artifact.WgslPath);
        var wgsl = File.ReadAllText(wgslPath);
        Assert.Contains($"@{artifact.Kernel.Stage.ToString().ToLowerInvariant()}", wgsl);
        Assert.DoesNotContain("var<push_constant>", wgsl);
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.Synchronize)}") {
            Assert.Contains("workgroupBarrier();", wgsl);
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(artifact.ManifestPath));
        var root = manifest.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("webgpu", root.GetProperty("kernelAbi").GetString());
        Assert.Equal(
            artifact.Kernel.Stage.ToString().ToLowerInvariant(),
            root.GetProperty("shaderStage").GetString());
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyVectors)}") {
            var resources = root.GetProperty("resources");
            Assert.All(resources.EnumerateArray(), resource => {
                Assert.Equal("float32x4", resource.GetProperty("elementType").GetString());
                Assert.Equal(16, resource.GetProperty("alignment").GetInt32());
                Assert.Equal(16, resource.GetProperty("size").GetInt32());
                Assert.Equal(16, resource.GetProperty("arrayStride").GetInt32());
            });
            Assert.Contains("array<vec4<f32>>", wgsl);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.AtomicWorkgroup)}") {
            Assert.Contains("var<workgroup>", wgsl);
            Assert.Contains("array<atomic<u32>, 32>", wgsl);
            Assert.Contains("atomicAdd", wgsl);
            Assert.Contains("atomicExchange", wgsl);
            Assert.Single(root.GetProperty("resources").EnumerateArray());
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(TextureShaders).FullName}.{nameof(TextureShaders.SampleAndLoad)}") {
            Assert.Contains("textureSampleLevel(texture", wgsl);
            Assert.Contains("textureSampleLevel(textureArray", wgsl);
            Assert.Contains("textureLoad(texture", wgsl);
            Assert.Contains("textureLoad(textureArray", wgsl);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyStructs)}") {
            var resource = root.GetProperty("resources")[0];
            Assert.EndsWith("ComputeShaders+Particle", resource.GetProperty("elementType").GetString());
            Assert.Equal(16, resource.GetProperty("alignment").GetInt32());
            Assert.Equal(32, resource.GetProperty("size").GetInt32());
            Assert.Equal(32, resource.GetProperty("arrayStride").GetInt32());
            Assert.Equal(2, resource.GetProperty("fields").GetArrayLength());
        }
    }
}

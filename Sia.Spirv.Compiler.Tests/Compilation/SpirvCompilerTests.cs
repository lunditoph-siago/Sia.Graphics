using Sia.Math;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Runtime;

namespace Sia.Spirv.Compiler.Tests.Compilation;

public sealed class SpirvCompilerTests
{
    [SpirvToolchainFact]
    [Trait("Category", "Toolchain")]
    public void CompileAssemblyProducesValidArtifactsAndPreservesStorageResults()
    {
        using var directory = new TestDirectory();
        var artifacts = Compile(directory.Path, SpirvCompilationTarget.Default);
        var expected = SpirvTestAssembly.Analyze().Kernels.Select(k => k.QualifiedName).Order().ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, artifacts.Select(a => a.Kernel.QualifiedName).Order());
        foreach (var artifact in artifacts) {
            var manifest = SpirvArtifactLoader.Load(artifact.ManifestPath).Manifest;
            Assert.Equal(artifact.Kernel.QualifiedName, manifest.SourceMethod);
            Assert.Equal(artifact.Kernel.Stage.ToString().ToLowerInvariant(), manifest.ShaderStage);
            Assert.Equal("webgpu", manifest.KernelAbi);
            // CompileAssembly already validates each binary with SPIRV-Tools.
            string wgsl = File.ReadAllText(Assert.IsType<string>(artifact.WgslPath));
            var entry = Assert.Single(WgslReader.Parse(wgsl).Functions, f => f.Stage is not null);
            Assert.Equal(manifest.ShaderStage, entry.Stage.ToString()!.ToLowerInvariant());
        }
        foreach (var row in IlSemanticCases.StorageResults) {
            string name = $"{typeof(MemorySemanticsShaders).FullName}.{row[0]}";
            var artifact = Assert.Single(artifacts, a => a.Kernel.QualifiedName == name);
            uint[] values = [(uint)row[1], (uint)row[2]];
            uint[] actual = WgslFixtureInterpreter.Execute(File.ReadAllText(artifact.WgslPath!)).Memory;
            Assert.True(values.SequenceEqual(actual),
                $"{name}: expected [{string.Join(", ", values)}], actual [{string.Join(", ", actual)}].");
        }
    }

    [SpirvToolchainFact]
    [Trait("Category", "Toolchain")]
    public void BoundedUniformBufferManifestMatchesShaderAndPacking()
    {
        using var directory = new TestDirectory();
        var target = SpirvCompilationTarget.Default with { ResourceLimits = SpirvTargetProfile.Default with {
            PreferUniformForBoundedReadOnlyBuffers = true
        } };
        var artifacts = Compile(directory.Path, target);
        var artifact = Assert.Single(artifacts, a => a.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyBoundedStructs)}");
        var manifest = SpirvArtifactLoader.Load(artifact.ManifestPath).Manifest;
        var source = Assert.Single(manifest.Resources, r => r.Name == "source");
        Assert.Equal("uniform-buffer", source.Kind);
        Assert.Equal(4, source.ElementCount);
        Assert.Equal(16, source.ArrayStride);
        Assert.Contains("buffer.storage_to_uniform", manifest.LegalizationStrategies!);
        var shader = WgslReader.Parse(File.ReadAllText(artifact.WgslPath!));
        Assert.Contains(shader.Globals, g => g.Space == AddressSpace.Uniform);

        var mapping = SpirvBufferMapping<ComputeShaders.PackedParticle>.Create(source);
        ComputeShaders.PackedParticle[] values = [new(new float3(1f, 2f, 3f), 7u)];
        byte[] packed = mapping.Pack(values);
        Assert.Equal(64, packed.Length);
        Assert.All(packed[16..], value => Assert.Equal(0, value));
    }

    private static IReadOnlyList<SpirvArtifact> Compile(string output, SpirvCompilationTarget target)
        => new SpirvCompiler().CompileAssembly(new(SpirvTestAssembly.Path, output) {
            ToolchainDirectory = SpirvTestToolchain.Directory,
            Target = target with { KernelAbi = SpirvKernelAbi.WebGpu },
            EmitWgsl = true,
            OptimizationLevel = 3
        });
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Sia.Math;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Runtime;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvCompilerTests
{
    [SpirvToolchainFact]
    public void CompileAssemblyProducesOptimizedSpirvAndWgsl()
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
                EmitLlvmIr = true,
                OptimizationLevel = 3
            };

            var artifacts = compiler.CompileAssembly(
                SpirvTestAssembly.Path,
                outputDirectory,
                options);

            var expectedKernels = SpirvTestAssembly.Analyze().Kernels
                .Select(static kernel => kernel.QualifiedName)
                .Order();
            var compiledKernels = artifacts
                .Select(static artifact => artifact.Kernel.QualifiedName)
                .Order();
            Assert.Equal(expectedKernels, compiledKernels);
            Assert.All(artifacts, artifact => AssertArtifact(artifact));
        }
        finally {
            if (System.IO.Directory.Exists(outputDirectory)) {
                System.IO.Directory.Delete(outputDirectory, true);
            }
        }
    }

    [SpirvToolchainFact]
    public void CompileAssemblyLowersBoundedStorageBufferToUniform()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "sia-spirv-tests",
            Guid.NewGuid().ToString("N"));
        try {
            var options = new SpirvCompilationOptions {
                ToolchainDirectory = SpirvTestToolchain.Directory,
                KernelAbi = SpirvKernelAbi.WebGpu,
                EmitWgsl = true,
                EmitLlvmIr = true,
                OptimizationLevel = 3,
                TargetProfile = SpirvTargetProfile.Default with {
                    PreferUniformForBoundedReadOnlyBuffers = true
                }
            };

            var artifacts = new SpirvCompiler().CompileAssembly(
                SpirvTestAssembly.Path,
                outputDirectory,
                options);
            var artifact = Assert.Single(artifacts, artifact =>
                artifact.Kernel.QualifiedName ==
                    $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyBoundedStructs)}");

            var llvm = File.ReadAllText(Assert.IsType<string>(artifact.LlvmIrPath));
            var wgsl = File.ReadAllText(Assert.IsType<string>(artifact.WgslPath));
            Assert.Contains(
                "target(\"spirv.VulkanBuffer\", [4 x %sia.struct.uniform], 2, 0)",
                llvm);
            Assert.Contains("var<uniform>", wgsl);

            var module = SpirvArtifactLoader.Load(artifact.ManifestPath);
            var source = Assert.Single(
                module.Manifest.Resources,
                static resource => resource.Name == "source");
            Assert.Equal("uniform-buffer", source.Kind);
            Assert.Equal(4, source.ElementCount);
            Assert.Equal(16, source.ArrayStride);
            Assert.Contains(
                "buffer.storage_to_uniform",
                module.Manifest.LegalizationStrategies!);

            var mapping = SpirvBufferMapping<ComputeShaders.PackedParticle>.Create(source);
            ComputeShaders.PackedParticle[] values = [
                new(new float3(1.0f, 2.0f, 3.0f), 7u)
            ];
            var packed = mapping.Pack(values);
            Assert.Equal(64, packed.Length);
            Assert.All(packed[16..], static value => Assert.Equal(0, value));
        }
        finally {
            if (System.IO.Directory.Exists(outputDirectory)) {
                System.IO.Directory.Delete(outputDirectory, true);
            }
        }
    }

    private static void AssertArtifact(SpirvArtifact artifact)
    {
        var wgslPath = Assert.IsType<string>(artifact.WgslPath);
        var wgsl = File.ReadAllText(wgslPath);
        var llvmPath = Assert.IsType<string>(artifact.LlvmIrPath);
        var llvm = File.ReadAllText(llvmPath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(artifact.ManifestPath));
        var manifestRoot = manifest.RootElement;
        Assert.DoesNotContain("alloca ", llvm);
        Assert.Contains($"@{artifact.Kernel.Stage.ToString().ToLowerInvariant()}", wgsl);
        Assert.DoesNotContain("var<push_constant>", wgsl);
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.Synchronize)}") {
            Assert.Contains("workgroupBarrier();", wgsl);
        }

        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyVectors)}") {
            Assert.Contains("array<vec4<f32>>", wgsl);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.AtomicWorkgroup)}") {
            Assert.Contains("var<workgroup>", wgsl);
            Assert.Contains("array<atomic<u32>, 32>", wgsl);
            Assert.Contains("atomicAdd", wgsl);
            Assert.Contains("atomicExchange", wgsl);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyPackedStructs)}") {
            Assert.Contains("%sia.struct.storage = type <{ <3 x float>, i32 }>", llvm);
            Assert.Contains("load <3 x float>, ptr addrspace(11)", llvm);
            Assert.DoesNotContain("bitcast i32", llvm);
            AssertStorageStructLayout(artifact, 16, 0, 12);

            var module = SpirvArtifactLoader.Load(artifact.ManifestPath);
            var mapping = SpirvBufferMapping<ComputeShaders.PackedParticle>.Create(
                module.Manifest,
                "source");
            ComputeShaders.PackedParticle[] values = [
                new(new float3(1.0f, 2.0f, 3.0f), 5u)
            ];

            Assert.Equal(16, mapping.GpuStride);
            Assert.Equal(16, mapping.Pack(values).Length);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyAlignedStructs)}") {
            Assert.Contains(
                "%sia.struct.storage = type <{ i32, [3 x i32], <3 x float>, [1 x i32] }>",
                llvm);
            AssertStorageStructLayout(artifact, 32, 0, 4, 16, 28);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ComputeShaders).FullName}.{nameof(ComputeShaders.CopyLogicalStructs)}") {
            Assert.Contains("%sia.struct.storage = type <{ <3 x float>, i32 }>", llvm);
            AssertStorageStructLayout(artifact, 16, 0, 12);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(TextureShaders).FullName}.{nameof(TextureShaders.SampleAndLoad)}") {
            Assert.Contains("textureSampleLevel(texture", wgsl);
            Assert.Contains("textureSampleLevel(textureArray", wgsl);
            Assert.Contains("textureLoad(texture", wgsl);
            Assert.Contains("textureLoad(textureArray", wgsl);
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(FullscreenVertexShaders).FullName}.{nameof(FullscreenVertexShaders.Vertex)}") {
            Assert.Contains("@builtin(vertex_index)", wgsl);
            Assert.Contains("@builtin(position)", wgsl);
            Assert.Contains("@location(0)", wgsl);
            Assert.Contains("vec2<f32>", wgsl);
            Assert.Contains("@interpolate(linear, centroid)", wgsl);
            Assert.Contains("@interpolate(flat)", wgsl);
            Assert.DoesNotContain("bool()", wgsl);
            Assert.DoesNotContain("undef", llvm);
            Assert.Contains(
                manifestRoot.GetProperty("stageInputs").EnumerateArray(),
                input => input.GetProperty("semantic").GetString() == "vertex-index");
            Assert.Contains(
                manifestRoot.GetProperty("stageOutputs").EnumerateArray(),
                output => output.GetProperty("semantic").GetString() == "position");
        }
        if (artifact.Kernel.QualifiedName ==
            $"{typeof(ExplicitFragmentShaders).FullName}.{nameof(ExplicitFragmentShaders.Fragment)}") {
            Assert.Contains("@builtin(position)", wgsl);
            Assert.Contains("@builtin(front_facing)", wgsl);
            Assert.Contains("@builtin(frag_depth)", wgsl);
            Assert.Contains("@location(0)", wgsl);
            Assert.Contains(
                manifestRoot.GetProperty("stageInputs").EnumerateArray(),
                input => input.GetProperty("semantic").GetString() == "fragment-position");
        }
    }

    private static void AssertStorageStructLayout(
        SpirvArtifact artifact,
        int arrayStride,
        params int[] offsets)
    {
        var spirv = SpirvTestToolchain.Disassemble(artifact.SpirvPath);
        var structName = Regex.Match(
            spirv,
            "OpName (?<id>%\\S+) \\\"sia\\.struct\\.storage\\\"");
        Assert.True(structName.Success, "The physical storage struct has no SPIR-V name.");
        var structId = structName.Groups["id"].Value;
        for (var index = 0; index < offsets.Length; index++) {
            Assert.Contains(
                $"OpMemberDecorate {structId} {index} Offset {offsets[index]}",
                spirv);
        }

        var runtimeArray = Regex.Match(
            spirv,
            $"(?m)^\\s*(?<id>%\\S+) = OpTypeRuntimeArray {Regex.Escape(structId)}\\s*$");
        Assert.True(runtimeArray.Success, "The physical storage struct has no runtime array.");
        Assert.Contains(
            $"OpDecorate {runtimeArray.Groups["id"].Value} ArrayStride {arrayStride}",
            spirv);
    }
}

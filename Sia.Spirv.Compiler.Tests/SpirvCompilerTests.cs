using Sia.Spirv.Compiler.Compilation;

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
                EmitLlvmIr = true
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

    private static void AssertArtifact(SpirvArtifact artifact)
    {
        var wgslPath = Assert.IsType<string>(artifact.WgslPath);
        var wgsl = File.ReadAllText(wgslPath);
        var llvmPath = Assert.IsType<string>(artifact.LlvmIrPath);
        var llvm = File.ReadAllText(llvmPath);
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
            $"{typeof(TextureShaders).FullName}.{nameof(TextureShaders.SampleAndLoad)}") {
            Assert.Contains("textureSampleLevel(texture", wgsl);
            Assert.Contains("textureSampleLevel(textureArray", wgsl);
            Assert.Contains("textureLoad(texture", wgsl);
            Assert.Contains("textureLoad(textureArray", wgsl);
        }
    }
}

using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation;

namespace Sia.Spirv.Compiler.Tests.Compilation;

public sealed class OfflineShaderCompilerTests
{
    [Fact]
    public void OfflineBackendRunsOnlyForArtifactsMissingFromTheCache()
    {
        using var directory = new TestDirectory();
        var request = new SpirvFileCompilationRequest(SpirvTestAssembly.Path, directory.Path) {
            EmitLlvmIr = false
        };
        var backend = new RecordingBackend();
        IReadOnlyList<SpirvArtifact> Compile() => OfflineShaderCompiler.Compile(request, "cached", _ => backend);

        var first = Compile();
        Assert.NotEmpty(first);
        Assert.All(first, artifact => Assert.False(artifact.CacheHit));
        Assert.Equal(first.Select(artifact => artifact.Kernel.QualifiedName), backend.Compiled);

        backend.Compiled.Clear();
        var cached = Compile();
        Assert.Equal(first.Count, cached.Count);
        Assert.All(cached, artifact => Assert.True(artifact.CacheHit));
        Assert.Empty(backend.Compiled);

        var missing = first[0];
        File.Delete(missing.ManifestPath);
        var rebuilt = Assert.Single(Compile(), artifact => !artifact.CacheHit);
        Assert.Equal(missing.ManifestPath, rebuilt.ManifestPath);
        Assert.Equal(missing.SpirvPath, rebuilt.SpirvPath);
        Assert.Equal(missing.Kernel.QualifiedName, Assert.Single(backend.Compiled));
    }

    private sealed class RecordingBackend : IOfflineShaderBackend
    {
        private readonly byte[] _binary = ShaderTranslator.WgslToSpirv(
            "@compute @workgroup_size(1) fn main() {}", SpirvCompilationTarget.Default);

        public SpirvManifestToolchain Versions { get; } = new("test-llvm", "test-spirv-tools");
        public List<string> Compiled { get; } = [];

        public void Compile(
            SpirvFileCompilationRequest request,
            SpirvKernel kernel,
            SpirvKernelAbiLayout layout,
            ShaderArtifactPaths paths)
        {
            Compiled.Add(kernel.QualifiedName);
            File.WriteAllBytes(paths.Spirv, _binary);
        }
    }
}

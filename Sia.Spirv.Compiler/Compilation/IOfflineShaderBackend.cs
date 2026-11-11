using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Compilation;

/// <summary>Generates one legalized shader's target files. The caller owns caching and publication.</summary>
internal interface IOfflineShaderBackend
{
    SpirvManifestToolchain Versions { get; }

    void Compile(SpirvFileCompilationRequest request, SpirvKernel kernel, ShaderArtifactPaths paths);
}

internal readonly record struct ShaderArtifactPaths(string Llvm, string RawLlvm, string Spirv, string Wgsl, string Manifest);

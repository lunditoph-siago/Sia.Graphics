using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Compilation;

internal interface IOfflineShaderBackend
{
    SpirvManifestToolchain Versions { get; }

    void Compile(
        SpirvFileCompilationRequest request,
        SpirvKernel kernel,
        SpirvKernelAbiLayout layout,
        ShaderArtifactPaths paths);
}

internal readonly record struct ShaderArtifactPaths(string Llvm, string RawLlvm, string Spirv, string Wgsl, string Manifest);

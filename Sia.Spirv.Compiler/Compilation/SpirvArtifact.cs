using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvArtifact(
    SpirvKernel Kernel,
    string SpirvPath,
    string? WgslPath,
    string ManifestPath,
    string? LlvmIrPath,
    bool CacheHit);

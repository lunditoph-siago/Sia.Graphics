namespace Sia.Spirv.Runtime;

public sealed record SpirvModuleArtifact(
    string SpirvPath,
    string ManifestPath,
    ReadOnlyMemory<byte> Bytecode,
    SpirvArtifactManifest Manifest);

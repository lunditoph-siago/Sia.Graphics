namespace Sia.Spirv.Compiler.Compilation;

internal sealed record SpirvArtifactManifest(
    int SchemaVersion,
    string EntryPoint,
    string SourceMethod,
    int MetadataToken,
    SpirvManifestWorkgroupSize WorkgroupSize,
    string TargetEnvironment,
    string SpirvVersion,
    IReadOnlyList<SpirvManifestResource> Resources,
    IReadOnlyList<SpirvManifestPushConstant> PushConstants,
    SpirvManifestToolchain Toolchain,
    string SourceHash,
    string KernelAbi,
    string ShaderStage);

internal sealed record SpirvManifestWorkgroupSize(uint X, uint Y, uint Z);

internal sealed record SpirvManifestResource(
    string Name,
    string Kind,
    string Access,
    string ElementType,
    int DescriptorSet,
    int Binding,
    int Alignment,
    int Size,
    int ArrayStride,
    IReadOnlyList<SpirvManifestStructField>? Fields = null);

internal sealed record SpirvManifestStructField(
    string Name,
    string Type,
    int Offset,
    int Alignment,
    int Size);

internal sealed record SpirvManifestPushConstant(
    string Name,
    string Type,
    int Offset,
    int Size);

internal sealed record SpirvManifestToolchain(string Llvm, string SpirvTools, string? Naga);

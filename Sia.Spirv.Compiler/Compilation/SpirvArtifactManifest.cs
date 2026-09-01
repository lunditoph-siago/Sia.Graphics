namespace Sia.Spirv.Compiler.Compilation;

internal sealed record SpirvArtifactManifest(
    string EntryPoint,
    string SourceMethod,
    int MetadataToken,
    SpirvManifestWorkgroupSize WorkgroupSize,
    string TargetEnvironment,
    string SpirvVersion,
    IReadOnlyList<SpirvManifestResource> Resources,
    IReadOnlyList<SpirvManifestPushConstant> PushConstants,
    IReadOnlyList<SpirvManifestStageIo> StageInputs,
    IReadOnlyList<SpirvManifestStageIo> StageOutputs,
    SpirvManifestToolchain Toolchain,
    string SourceHash,
    string KernelAbi,
    string ShaderStage,
    string? LlvmPasses = null,
    IReadOnlyList<string>? LegalizationStrategies = null);

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
    IReadOnlyList<SpirvManifestStructField>? Fields = null,
    int? ElementCount = null);

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

internal sealed record SpirvManifestStageIo(
    string Name,
    string Semantic,
    string Type,
    uint? Location,
    bool Flat,
    string? Interpolation,
    string? Sampling);

internal sealed record SpirvManifestToolchain(string Llvm, string SpirvTools, string? Naga);

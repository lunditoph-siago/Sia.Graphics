namespace Sia.Spirv.Runtime;

public sealed record SpirvArtifactManifest(
    string EntryPoint,
    string SourceMethod,
    int MetadataToken,
    SpirvWorkgroupSize WorkgroupSize,
    string TargetEnvironment,
    string SpirvVersion,
    IReadOnlyList<SpirvResourceBinding> Resources,
    IReadOnlyList<SpirvPushConstant> PushConstants,
    SpirvToolchainInfo Toolchain,
    string SourceHash,
    string KernelAbi = "vulkan",
    string ShaderStage = "compute");

namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvCompilationOptions
{
    public string? ToolchainDirectory { get; init; }

    public string TargetEnvironment { get; init; } = "vulkan1.2";

    public SpirvKernelAbi KernelAbi { get; init; } = SpirvKernelAbi.Vulkan;

    public bool EmitWgsl { get; init; }

    public int OptimizationLevel { get; init; } = 2;

    public bool EmitLlvmIr { get; init; } = true;
}

namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvCompilationOptions
{
    public string? ToolchainDirectory { get; init; }

    public string TargetEnvironment { get; init; } = "vulkan1.2";

    public int OptimizationLevel { get; init; } = 2;

    public bool EmitLlvmIr { get; init; } = true;
}

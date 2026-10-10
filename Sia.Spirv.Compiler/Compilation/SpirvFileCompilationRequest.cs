namespace Sia.Spirv.Compiler.Compilation;

/// <summary>Offline file/process composition with the same default target as memory compilation.</summary>
public sealed record SpirvFileCompilationRequest(string AssemblyPath, string OutputDirectory)
{
    public SpirvCompilationTarget Target { get; init; } = SpirvCompilationTarget.Default;
    public string? ToolchainDirectory { get; init; }
    public bool EmitWgsl { get; init; }
    public int OptimizationLevel { get; init; } = 2;
    public string? LlvmPasses { get; init; }
    public bool EmitLlvmIr { get; init; } = true;
}

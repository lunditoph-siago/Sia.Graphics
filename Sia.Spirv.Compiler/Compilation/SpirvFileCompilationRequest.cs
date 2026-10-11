namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvFileCompilationRequest(string AssemblyPath, string OutputDirectory)
{
    public SpirvCompilationTarget Target { get; init; } = SpirvCompilationTarget.Default;
    public string? ToolchainDirectory { get; init; }
    public bool EmitWgsl { get; init; }
    public int OptimizationLevel { get; init; } = 2;
    public string? LlvmPasses { get; init; }
    public bool EmitLlvmIr { get; init; } = true;
}

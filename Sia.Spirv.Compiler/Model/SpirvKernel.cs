using Sia.Spirv.Compiler.IL;

namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvKernel(
    string DeclaringType,
    string Name,
    int MetadataToken,
    SpirvWorkgroupSize WorkgroupSize,
    IReadOnlyList<SpirvKernelParameter> Parameters,
    CilControlFlowGraph ControlFlowGraph)
{
    public string QualifiedName => $"{DeclaringType}.{Name}";
}

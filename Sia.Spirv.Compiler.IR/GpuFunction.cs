namespace Sia.Spirv.Compiler.IR;

public sealed record GpuFunction(
    string Name,
    GpuType ReturnType,
    IReadOnlyList<GpuValue> Parameters,
    IReadOnlyList<GpuBasicBlock> Blocks);

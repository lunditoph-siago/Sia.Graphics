namespace Sia.Spirv.Compiler.IR;

public sealed record GpuBasicBlock(
    int Id,
    IReadOnlyList<GpuInstruction> Instructions);

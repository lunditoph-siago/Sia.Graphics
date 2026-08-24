namespace Sia.Spirv.Compiler.IR;

public sealed record GpuInstruction(
    GpuOperation Operation,
    GpuValue? Result,
    IReadOnlyList<GpuValue> Operands,
    object? Immediate = null);

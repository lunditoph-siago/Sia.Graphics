namespace Sia.Spirv.Compiler.IR;

public sealed record GpuBufferType(
    GpuType ElementType,
    GpuBufferKind Kind) : GpuType;

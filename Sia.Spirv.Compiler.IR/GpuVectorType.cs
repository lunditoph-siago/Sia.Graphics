namespace Sia.Spirv.Compiler.IR;

public sealed record GpuVectorType(GpuType ElementType, int Length) : GpuType;

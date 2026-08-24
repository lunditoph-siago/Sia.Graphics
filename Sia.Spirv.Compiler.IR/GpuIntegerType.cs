namespace Sia.Spirv.Compiler.IR;

public sealed record GpuIntegerType(int Width, bool IsSigned) : GpuType;

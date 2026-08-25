namespace Sia.Spirv.Compiler.IR;

public sealed record GpuStructField(string Name, GpuType Type, int Offset);

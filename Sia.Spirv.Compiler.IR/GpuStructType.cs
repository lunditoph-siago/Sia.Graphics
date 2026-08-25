namespace Sia.Spirv.Compiler.IR;

public sealed record GpuStructType(
    string Name,
    IReadOnlyList<GpuStructField> Fields) : GpuType;

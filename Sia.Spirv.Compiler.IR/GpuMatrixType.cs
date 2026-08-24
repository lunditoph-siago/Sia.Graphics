namespace Sia.Spirv.Compiler.IR;

public sealed record GpuMatrixType(GpuVectorType ColumnType, int ColumnCount) : GpuType;

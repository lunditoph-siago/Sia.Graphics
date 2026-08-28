namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvStructLayout(
    string Name,
    int Alignment,
    int Size,
    int ArrayStride,
    IReadOnlyList<SpirvStructField> Fields);

public sealed record SpirvStructField(
    string Name,
    SpirvScalarType Type,
    int Offset,
    int Alignment,
    int Size);

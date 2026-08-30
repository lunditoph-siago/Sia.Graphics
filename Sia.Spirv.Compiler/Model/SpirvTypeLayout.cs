namespace Sia.Spirv.Compiler.Model;

internal static class SpirvTypeLayout
{
    public static int GetAlignment(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 or SpirvScalarType.UInt32 or SpirvScalarType.Float32 => 4,
        SpirvScalarType.Int32x2 or SpirvScalarType.UInt32x2 or SpirvScalarType.Float32x2 => 8,
        SpirvScalarType.Int32x3 or SpirvScalarType.Int32x4 or
            SpirvScalarType.UInt32x3 or SpirvScalarType.UInt32x4 or
            SpirvScalarType.Float32x3 or SpirvScalarType.Float32x4 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static int GetSize(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 or SpirvScalarType.UInt32 or SpirvScalarType.Float32 => 4,
        SpirvScalarType.Int32x2 or SpirvScalarType.UInt32x2 or SpirvScalarType.Float32x2 => 8,
        SpirvScalarType.Int32x3 or SpirvScalarType.UInt32x3 or SpirvScalarType.Float32x3 => 12,
        SpirvScalarType.Int32x4 or SpirvScalarType.UInt32x4 or SpirvScalarType.Float32x4 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static int GetArrayStride(SpirvScalarType type) =>
        AlignUp(GetSize(type), GetAlignment(type));

    public static string GetName(SpirvScalarType type) => type switch {
        SpirvScalarType.Boolean => "boolean",
        SpirvScalarType.Int32 => "int32",
        SpirvScalarType.UInt32 => "uint32",
        SpirvScalarType.Float32 => "float32",
        SpirvScalarType.Int32x2 => "int32x2",
        SpirvScalarType.Int32x3 => "int32x3",
        SpirvScalarType.Int32x4 => "int32x4",
        SpirvScalarType.UInt32x2 => "uint32x2",
        SpirvScalarType.UInt32x3 => "uint32x3",
        SpirvScalarType.UInt32x4 => "uint32x4",
        SpirvScalarType.Float32x2 => "float32x2",
        SpirvScalarType.Float32x3 => "float32x3",
        SpirvScalarType.Float32x4 => "float32x4",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

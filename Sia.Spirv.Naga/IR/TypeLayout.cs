namespace Sia.Spirv.Naga.IR;

public readonly record struct TypeLayout(uint Alignment, uint Size, bool IsRuntimeSized = false)
{
    public uint Stride => RoundUp(Alignment, Size);

    public static uint RoundUp(uint alignment, uint size)
    {
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ShaderException(DiagnosticStage.Validation, "Alignment must be a nonzero power of two.");
        return checked((size + alignment - 1) & ~(alignment - 1));
    }

    public static TypeLayout Of(ShaderType type) => type switch
    {
        ShaderType.Scalar s when s.Width is 1 or 2 or 4 or 8 => new((uint)s.Width, (uint)s.Width),
        ShaderType.Atomic a => Of(a.Component),
        ShaderType.Vector v when v.Size is >= 2 and <= 4 => new((uint)(v.Component.Width * (v.Size == 3 ? 4 : v.Size)), (uint)(v.Component.Width * v.Size)),
        ShaderType.Matrix m => Matrix(m),
        ShaderType.CooperativeMatrix m => new(checked((uint)(m.Rows * m.Component.Width)), checked((uint)(m.Rows * m.Columns * m.Component.Width))),
        ShaderType.Array a => Array(a),
        ShaderType.Structure s => Structure(s),
        _ => throw new ShaderException(DiagnosticStage.Validation, $"Type {type.GetType().Name} has no data layout.")
    };

    private static TypeLayout Matrix(ShaderType.Matrix matrix)
    {
        var column = Of(new ShaderType.Vector(matrix.Rows, matrix.Component));
        return new(column.Alignment, checked(column.Stride * (uint)matrix.Columns));
    }
    private static TypeLayout Array(ShaderType.Array array)
    {
        if (array.OverrideLength is not null) throw new ShaderException(DiagnosticStage.Validation, "Array layout requires pipeline constant resolution.");
        var element = Of(array.Element);
        uint stride = array.Stride ?? element.Stride;
        if (element.IsRuntimeSized || stride < element.Size || stride % element.Alignment != 0)
            throw new ShaderException(DiagnosticStage.Validation, "Invalid array element layout or stride.");
        return new(element.Alignment, checked(stride * (array.Length ?? 1)), array.Length is null);
    }
    private static TypeLayout Structure(ShaderType.Structure structure)
    {
        uint size = 0, alignment = 1; bool runtime = false;
        for (int i = 0; i < structure.Members.Count; i++)
        {
            var member = structure.Members[i]; var layout = Of(member.Type);
            uint memberAlignment = member.Alignment ?? layout.Alignment;
            if (memberAlignment < layout.Alignment || (memberAlignment & (memberAlignment - 1)) != 0 || memberAlignment == 0)
                throw new ShaderException(DiagnosticStage.Validation, "Member alignment must be a power of two at least as large as its type alignment.");
            uint offset = member.Offset ?? RoundUp(memberAlignment, size);
            if (offset < size || offset % memberAlignment != 0)
                throw new ShaderException(DiagnosticStage.Validation, "Overlapping or misaligned structure member.");
            if (layout.IsRuntimeSized && i != structure.Members.Count - 1)
                throw new ShaderException(DiagnosticStage.Validation, "Runtime array must be the final structure member.");
            uint memberSize = member.Size ?? layout.Size;
            if (memberSize < layout.Size) throw new ShaderException(DiagnosticStage.Validation, "Member size is smaller than its type.");
            size = checked(offset + memberSize); alignment = System.Math.Max(alignment, memberAlignment); runtime = layout.IsRuntimeSized;
        }
        return new(alignment, RoundUp(alignment, size), runtime);
    }
}

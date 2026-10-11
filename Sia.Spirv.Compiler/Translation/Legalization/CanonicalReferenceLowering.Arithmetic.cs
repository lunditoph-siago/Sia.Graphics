using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static partial class CanonicalReferenceLowering
{
    private static ShaderType? Container(Shape shape)
    {
        if (shape.Parts.Length == 0) return null;
        ShaderType type = ((ShaderType.Pointer)shape.Root.Type).Base;
        foreach (var part in shape.Parts[..^1])
            type = part.Member is { } member ? ((ShaderType.Structure)type).Members.Single(m => m.Name == member).Type : type switch {
                ShaderType.Array array => array.Element, ShaderType.BindingArray array => array.Element,
                ShaderType.Vector vector => vector.Component, ShaderType.Matrix matrix => new ShaderType.Vector(matrix.Rows, matrix.Component),
                _ => throw new ShaderException(DiagnosticStage.Validation, "Invalid pointer offset projection.")
            };
        return type;
    }

    private static SsaValue ConvertIndex(SsaValue value, ShaderType.Scalar type, Func<ShaderType, ValueOperation, SsaValue> emit)
    {
        if (value.Type == type) return value;
        if (type.Kind == ScalarKind.Sint)
            return emit(type, new ValueOperation.Convert(ConvertIndex(value, type with { Kind = ScalarKind.Uint }, emit), true));
        var source = (ShaderType.Scalar)value.Type;
        if (source.Kind == ScalarKind.Sint) value = emit(source with { Kind = ScalarKind.Uint }, new ValueOperation.Convert(value, true));
        return value.Type == type ? value : emit(type, new ValueOperation.Convert(value, false));
    }

    private static SsaValue SignedOffset(SsaValue value, ShaderType.Scalar type, Func<ShaderType, ValueOperation, SsaValue> emit)
    {
        var source = (ShaderType.Scalar)value.Type;
        if (source.Width == type.Width) return ConvertIndex(value, type, emit);
        // Element is signed irrespective of its declared signedness. Interpret
        // the source bit pattern before widening, then add in the index domain.
        if (source.Kind != ScalarKind.Sint) value = emit(source with { Kind = ScalarKind.Sint }, new ValueOperation.Convert(value, true));
        value = emit(type with { Kind = ScalarKind.Sint }, new ValueOperation.Convert(value, false));
        return emit(type, new ValueOperation.Convert(value, true));
    }
}

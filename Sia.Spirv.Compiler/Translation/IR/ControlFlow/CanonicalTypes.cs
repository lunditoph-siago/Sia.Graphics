namespace Sia.Spirv.Compiler.Translation.IR.ControlFlow;

internal static class CanonicalTypes
{
    public static bool Resource(ShaderType type) => type is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure or ShaderType.BindingArray;

    public static bool Data(ShaderType type) => type switch {
        ShaderType.Scalar s => s.Kind is not (ScalarKind.AbstractInt or ScalarKind.AbstractFloat),
        ShaderType.Vector v => Data(v.Component), ShaderType.Matrix m => Data(m.Component),
        ShaderType.Array { Length: not null, OverrideLength: null } a => Data(a.Element),
        ShaderType.Structure s => s.Members.All(m => Data(m.Type)), _ => false
    };
}

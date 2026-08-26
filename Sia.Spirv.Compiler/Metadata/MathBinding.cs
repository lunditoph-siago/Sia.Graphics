using System.Reflection.Metadata;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Metadata;

/// <summary>
/// Recognizes calls into <c>Sia.Math.float3</c>/<c>Sia.Math.math</c> as GPU
/// intrinsics, purely by declaring-type name + method name + parameter
/// shape — never by attribute. Unlike <see cref="IntrinsicCatalog"/>,
/// there is no metadata to open: <c>Sia.Math</c> is a real, independent
/// SIMD library that must not be asked to carry SPIR-V-specific
/// annotations just so its own shader-side bindings can find them. This
/// table is therefore the single source of truth for which <c>float3</c>
/// members this compiler understands; a call shape it doesn't recognize
/// falls through to "not a supported GPU intrinsic" exactly like any
/// other unrecognized call.
/// </summary>
internal static class MathBinding
{
    private const string Float3TypeName = "Sia.Math.float3";
    private const string MathTypeName = "Sia.Math.math";
    private const string FloatTypeName = "System.Single";

    public static IntrinsicKind? Resolve(
        string declaringType,
        string name,
        MethodSignature<KernelType> signature)
    {
        if (declaringType == Float3TypeName) {
            return ResolveFloat3Member(name, signature);
        }
        if (declaringType == MathTypeName) {
            return ResolveMathFunction(name, signature);
        }
        return null;
    }

    private static IntrinsicKind? ResolveFloat3Member(
        string name,
        MethodSignature<KernelType> signature)
    {
        var parameters = signature.ParameterTypes;
        if (name == ".ctor") {
            return parameters.Length switch {
                3 when IsFloat3Params(parameters, FloatTypeName, FloatTypeName, FloatTypeName) =>
                    IntrinsicKind.Float3Construct,
                1 when IsFloat3Params(parameters, FloatTypeName) => IntrinsicKind.Float3Broadcast,
                _ => null
            };
        }
        if (!signature.Header.IsInstance) {
            return name switch {
                "op_Addition" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) =>
                    IntrinsicKind.Float3Add,
                "op_Subtraction" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) =>
                    IntrinsicKind.Float3Subtract,
                "op_UnaryNegation" when IsFloat3Params(parameters, Float3TypeName) =>
                    IntrinsicKind.Float3Negate,
                "op_Multiply" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) =>
                    IntrinsicKind.Float3MultiplyVector,
                "op_Multiply" when IsFloat3Params(parameters, Float3TypeName, FloatTypeName) =>
                    IntrinsicKind.Float3MultiplyScalar,
                "op_Division" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) =>
                    IntrinsicKind.Float3DivideVector,
                "op_Division" when IsFloat3Params(parameters, Float3TypeName, FloatTypeName) =>
                    IntrinsicKind.Float3DivideScalar,
                _ => null
            };
        }
        if (parameters.Length != 0) {
            return null;
        }
        return name switch {
            "get_x" => IntrinsicKind.Float3GetX,
            "get_y" => IntrinsicKind.Float3GetY,
            "get_z" => IntrinsicKind.Float3GetZ,
            _ => null
        };
    }

    private static IntrinsicKind? ResolveMathFunction(
        string name,
        MethodSignature<KernelType> signature)
    {
        var parameters = signature.ParameterTypes;
        return name switch {
            "dot" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) => IntrinsicKind.Float3Dot,
            "cross" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) => IntrinsicKind.Float3Cross,
            "normalize" when IsFloat3Params(parameters, Float3TypeName) => IntrinsicKind.Float3Normalize,
            "min" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) => IntrinsicKind.Float3Min,
            "max" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) => IntrinsicKind.Float3Max,
            "reflect" when IsFloat3Params(parameters, Float3TypeName, Float3TypeName) => IntrinsicKind.Float3Reflect,
            _ => null
        };
    }

    private static bool IsFloat3Params(
        IReadOnlyList<KernelType> parameters,
        params ReadOnlySpan<string> expectedNames)
    {
        if (parameters.Count != expectedNames.Length) {
            return false;
        }
        for (var index = 0; index < expectedNames.Length; index++) {
            if (parameters[index].Name != expectedNames[index]) {
                return false;
            }
        }
        return true;
    }
}

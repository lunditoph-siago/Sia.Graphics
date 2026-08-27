using System.Reflection.Metadata;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Metadata;

/// <summary>
/// Recognizes supported <c>Sia.Math</c> vector, matrix, and math calls as GPU
/// intrinsics by declaring-type/method/parameter shape, never by
/// attribute — <c>Sia.Math</c> is an independent SIMD library and must
/// not carry SPIR-V-specific metadata. An unrecognized shape falls
/// through to "not a supported GPU intrinsic" like any other call.
/// </summary>
internal static class MathBinding
{
    private const string MathTypeName = "Sia.Math.math";
    private const string FloatTypeName = "System.Single";
    private const string IntTypeName = "System.Int32";
    private const string UIntTypeName = "System.UInt32";
    private const string BoolTypeName = "System.Boolean";

    public static IntrinsicKind? Resolve(
        string declaringType,
        string name,
        MethodSignature<KernelType> signature)
    {
        if (TryGetVectorShape(declaringType, out _, out _) ||
            TryGetMatrixShape(declaringType, out _, out _)) {
            return ResolveValueMember(declaringType, name, signature);
        }
        if (declaringType == MathTypeName) {
            return ResolveMathFunction(name, signature);
        }
        return null;
    }

    private static IntrinsicKind? ResolveValueMember(
        string declaringType,
        string name,
        MethodSignature<KernelType> signature)
    {
        var parameters = signature.ParameterTypes;
        if (name == ".ctor") {
            return IsSupportedConstructor(declaringType, parameters)
                ? IntrinsicKind.MathConstruct
                : null;
        }
        if (!signature.Header.IsInstance) {
            return name switch {
                "op_Addition" when IsArithmeticShape(declaringType, parameters) => IntrinsicKind.MathAdd,
                "op_Subtraction" when IsArithmeticShape(declaringType, parameters) => IntrinsicKind.MathSubtract,
                "op_UnaryNegation" when IsSignedArithmeticType(declaringType) &&
                    IsParams(parameters, declaringType) => IntrinsicKind.MathNegate,
                "op_Multiply" when IsArithmeticShape(declaringType, parameters) => IntrinsicKind.MathMultiply,
                "op_Division" when IsArithmeticShape(declaringType, parameters) => IntrinsicKind.MathDivide,
                _ => null
            };
        }
        if (parameters.Length != 0) {
            return null;
        }
        return name is "get_x" or "get_y" or "get_z" or "get_w"
            ? IntrinsicKind.MathGetComponent
            : null;
    }

    private static IntrinsicKind? ResolveMathFunction(
        string name,
        MethodSignature<KernelType> signature)
    {
        var parameters = signature.ParameterTypes;
        if (TryGetVectorShape(signature.ReturnType.Name, out _, out _) ||
            TryGetMatrixShape(signature.ReturnType.Name, out _, out _)) {
            if (name == signature.ReturnType.Name["Sia.Math.".Length..] &&
                IsSupportedConstructor(signature.ReturnType.Name, parameters)) {
                return IntrinsicKind.MathConstruct;
            }
        }
        return name switch {
            "asfloat" when IsAsFloat(parameters, signature.ReturnType.Name) => IntrinsicKind.AsFloat,
            "f16tof32" when IsF16ToF32(parameters, signature.ReturnType.Name) => IntrinsicKind.UnpackHalf,
            "sqrt" when IsSameFloatShape(parameters, 1) => IntrinsicKind.Sqrt,
            "sin" when IsSameFloatShape(parameters, 1) => IntrinsicKind.Sin,
            "cos" when IsSameFloatShape(parameters, 1) => IntrinsicKind.Cos,
            "pow" when IsSameFloatShape(parameters, 2) => IntrinsicKind.Pow,
            "abs" when IsSameFloatShape(parameters, 1) => IntrinsicKind.Abs,
            "rsqrt" when IsSameFloatShape(parameters, 1) => IntrinsicKind.InverseSqrt,
            "min" when IsSameFloatShape(parameters, 2) => IntrinsicKind.MathMin,
            "max" when IsSameFloatShape(parameters, 2) => IntrinsicKind.MathMax,
            "clamp" when IsSameFloatShape(parameters, 3) => IntrinsicKind.MathClamp,
            "saturate" when IsSameFloatShape(parameters, 1) => IntrinsicKind.MathSaturate,
            "select" when IsSelect(parameters, signature.ReturnType.Name) => IntrinsicKind.Select,
            "dot" when IsSameVectorShape(parameters, 2) => IntrinsicKind.MathDot,
            "cross" when IsParams(parameters, "Sia.Math.float3", "Sia.Math.float3") => IntrinsicKind.MathCross,
            "normalize" when IsSameVectorShape(parameters, 1) => IntrinsicKind.MathNormalize,
            "reflect" when IsSameVectorShape(parameters, 2) => IntrinsicKind.MathReflect,
            "any" when IsBooleanVector(parameters) => IntrinsicKind.MathAny,
            "all" when IsBooleanVector(parameters) => IntrinsicKind.MathAll,
            "mul" when IsSupportedMul(parameters, signature.ReturnType.Name) => IntrinsicKind.MathMul,
            "transpose" when parameters.Length == 1 &&
                TryGetMatrixShape(parameters[0].Name, out _, out _) => IntrinsicKind.MathTranspose,
            _ => null
        };
    }

    private static bool IsSupportedConstructor(
        string typeName,
        IReadOnlyList<KernelType> parameters)
    {
        if (TryGetVectorShape(typeName, out var scalarType, out var length)) {
            return IsParams(parameters, Enumerable.Repeat(scalarType, length).ToArray()) ||
                IsParams(parameters, scalarType);
        }
        if (!TryGetMatrixShape(typeName, out var rows, out var columns)) {
            return false;
        }
        var columnType = $"Sia.Math.float{rows}";
        return IsParams(parameters, Enumerable.Repeat(columnType, columns).ToArray()) ||
            IsParams(parameters, Enumerable.Repeat(FloatTypeName, rows * columns).ToArray()) ||
            IsParams(parameters, FloatTypeName);
    }

    private static bool IsArithmeticShape(string typeName, IReadOnlyList<KernelType> parameters)
    {
        if (TryGetMatrixShape(typeName, out _, out _)) {
            return IsParams(parameters, typeName, typeName) ||
                IsParams(parameters, typeName, FloatTypeName) ||
                IsParams(parameters, FloatTypeName, typeName);
        }
        return TryGetVectorShape(typeName, out var scalarType, out _) &&
            scalarType != BoolTypeName &&
            (IsParams(parameters, typeName, typeName) ||
             IsParams(parameters, typeName, scalarType) ||
             IsParams(parameters, scalarType, typeName));
    }

    private static bool IsSignedArithmeticType(string typeName) =>
        TryGetMatrixShape(typeName, out _, out _) ||
        TryGetVectorShape(typeName, out var scalarType, out _) &&
        scalarType is FloatTypeName or IntTypeName;

    private static bool IsSameFloatShape(IReadOnlyList<KernelType> parameters, int count)
    {
        if (parameters.Count != count) {
            return false;
        }
        var name = parameters[0].Name;
        if (name != FloatTypeName && !TryGetFloatVectorLength(name, out _)) {
            return false;
        }
        return parameters.All(parameter => parameter.Name == name);
    }

    private static bool IsSameVectorShape(IReadOnlyList<KernelType> parameters, int count) =>
        parameters.Count == count &&
        TryGetFloatVectorLength(parameters[0].Name, out _) &&
        parameters.All(parameter => parameter.Name == parameters[0].Name);

    private static bool IsBooleanVector(IReadOnlyList<KernelType> parameters) =>
        parameters.Count == 1 &&
        TryGetVectorShape(parameters[0].Name, out var scalarType, out _) &&
        scalarType == BoolTypeName;

    private static bool IsAsFloat(IReadOnlyList<KernelType> parameters, string returnType)
    {
        if (parameters.Count != 1) {
            return false;
        }
        if (returnType == FloatTypeName) {
            return parameters[0].Name is IntTypeName or UIntTypeName;
        }
        return TryGetVectorShape(returnType, out var resultScalar, out var resultLength) &&
            resultScalar == FloatTypeName &&
            TryGetVectorShape(parameters[0].Name, out var inputScalar, out var inputLength) &&
            inputScalar is IntTypeName or UIntTypeName && inputLength == resultLength;
    }

    private static bool IsF16ToF32(IReadOnlyList<KernelType> parameters, string returnType)
    {
        if (parameters.Count != 1) {
            return false;
        }
        if (returnType == FloatTypeName) {
            return parameters[0].Name == UIntTypeName;
        }
        return TryGetVectorShape(returnType, out var resultScalar, out var resultLength) &&
            resultScalar == FloatTypeName &&
            TryGetVectorShape(parameters[0].Name, out var inputScalar, out var inputLength) &&
            inputScalar == UIntTypeName && inputLength == resultLength;
    }

    private static bool IsSelect(IReadOnlyList<KernelType> parameters, string returnType)
    {
        if (parameters.Count != 3 || parameters[0].Name != returnType ||
            parameters[1].Name != returnType) {
            return false;
        }
        if (returnType is BoolTypeName or IntTypeName or UIntTypeName or FloatTypeName) {
            return parameters[2].Name == BoolTypeName;
        }
        if (!TryGetVectorShape(returnType, out _, out var length)) {
            return false;
        }
        return parameters[2].Name == BoolTypeName ||
            TryGetVectorShape(parameters[2].Name, out var conditionScalar, out var conditionLength) &&
            conditionScalar == BoolTypeName && conditionLength == length;
    }

    private static bool IsSupportedMul(IReadOnlyList<KernelType> parameters, string returnType)
    {
        if (parameters.Count != 2) {
            return false;
        }
        var left = parameters[0].Name;
        var right = parameters[1].Name;
        return (TryGetMatrixShape(left, out _, out _) || TryGetFloatVectorLength(left, out _)) &&
            (TryGetMatrixShape(right, out _, out _) || TryGetFloatVectorLength(right, out _)) &&
            (TryGetMatrixShape(returnType, out _, out _) || TryGetFloatVectorLength(returnType, out _));
    }

    private static bool TryGetFloatVectorLength(string typeName, out int length)
    {
        var result = TryGetVectorShape(typeName, out var scalarType, out length);
        return result && scalarType == FloatTypeName;
    }

    private static bool TryGetVectorShape(
        string typeName,
        out string scalarType,
        out int length)
    {
        scalarType = typeName.StartsWith("Sia.Math.bool", StringComparison.Ordinal) ? BoolTypeName :
            typeName.StartsWith("Sia.Math.int", StringComparison.Ordinal) ? IntTypeName :
            typeName.StartsWith("Sia.Math.uint", StringComparison.Ordinal) ? UIntTypeName :
            typeName.StartsWith("Sia.Math.float", StringComparison.Ordinal) ? FloatTypeName : string.Empty;
        length = typeName.Length > "Sia.Math.".Length ? typeName[^1] - '0' : 0;
        return scalarType.Length != 0 && length is >= 2 and <= 4 &&
            typeName == $"Sia.Math.{GetVectorPrefix(scalarType)}{length}";
    }

    private static string GetVectorPrefix(string scalarType) => scalarType switch {
        BoolTypeName => "bool",
        IntTypeName => "int",
        UIntTypeName => "uint",
        FloatTypeName => "float",
        _ => throw new ArgumentOutOfRangeException(nameof(scalarType))
    };

    private static bool TryGetMatrixShape(string typeName, out int rows, out int columns)
    {
        rows = 0;
        columns = 0;
        if (typeName.Length != "Sia.Math.float2x2".Length ||
            !typeName.StartsWith("Sia.Math.float", StringComparison.Ordinal)) {
            return false;
        }
        rows = typeName[^3] - '0';
        columns = typeName[^1] - '0';
        return rows is >= 2 and <= 4 && columns is >= 2 and <= 4;
    }

    private static bool IsParams(
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

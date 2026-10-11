using System.Numerics;

namespace Sia.Spirv.Compiler.Translation.IR;

internal static class NumericConversions
{
    // WGSL float-to-integer casts clamp to a value representable by both types.
    internal static (double Minimum, double Maximum) IntegerFloatBounds(ShaderType.Scalar source, ShaderType.Scalar target)
    {
        int bits = target.Width * 8;
        BigInteger maximum = (BigInteger.One << (target.Kind == ScalarKind.Uint ? bits : bits - 1)) - 1;
        double minimum = target.Kind == ScalarKind.Uint ? 0 : -Math.Pow(2, bits - 1);
        double upper = source.Width switch
        {
            2 => (double)(Half)(double)maximum,
            4 => (double)(float)maximum,
            8 => (double)maximum,
            _ => throw new ShaderException(DiagnosticStage.Validation, "Unsupported floating-point width.")
        };
        if (double.IsPositiveInfinity(upper)) upper = (double)Half.MaxValue;
        if (new BigInteger(upper) > maximum)
            upper = source.Width switch { 2 => (double)Half.BitDecrement((Half)upper), 4 => MathF.BitDecrement((float)upper), _ => Math.BitDecrement(upper) };
        if (source.Width == 2) minimum = Math.Max(minimum, (double)Half.MinValue);
        return (minimum, upper);
    }

    internal static object FloatValue(double value, ShaderType.Scalar type) => type.Width switch
    {
        2 => (object)(Half)value, 4 => (float)value, _ => value
    };
}

using System.Globalization;
using System.Numerics;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Front;

internal static class WgslNumbers
{
    internal static readonly ShaderType.Scalar AbstractInt = new(ScalarKind.AbstractInt, 8);
    internal static readonly ShaderType.Scalar AbstractFloat = new(ScalarKind.AbstractFloat, 8);

    public static Expression.Literal Parse(string source, SourceSpan span)
    {
        try
        {
            if (source is "true" or "false") return Expression.Bool(source == "true") with { Span = span };
            bool hex = source.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            bool floating = source.Contains('.') || source.IndexOfAny(hex ? ['p', 'P'] : ['e', 'E']) >= 0;
            char suffix = source[^1];
            bool wide = source.EndsWith("li", StringComparison.Ordinal) || source.EndsWith("lu", StringComparison.Ordinal) || source.EndsWith("lf", StringComparison.Ordinal);
            bool hasSuffix = suffix is 'i' or 'u' || suffix is 'f' or 'h' && (!hex || floating);
            hasSuffix |= wide;
            string text = hasSuffix ? source[..^(wide ? 2 : 1)] : source;
            floating |= hasSuffix && suffix is 'f' or 'h';
            if (floating)
            {
                if (hasSuffix && suffix is 'i' or 'u') throw new FormatException();
                double value = hex ? HexFloat(text) : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(value)) throw new OverflowException();
                if (wide && suffix == 'f') return new(value, new ShaderType.Scalar(ScalarKind.Float, 8)) { Span = span };
                if (suffix == 'f' && hasSuffix)
                {
                    float f = (float)value; if (!float.IsFinite(f)) throw new OverflowException();
                    return new(f, ShaderType.F32) { Span = span };
                }
                if (suffix == 'h' && hasSuffix)
                {
                    Half h = (Half)value; if (!Half.IsFinite(h)) throw new OverflowException();
                    return new(h, ShaderType.F16) { Span = span };
                }
                return new(value, AbstractFloat) { Span = span };
            }
            if (!hex && text.Length > 1 && text[0] == '0') throw new FormatException();
            BigInteger integer = hex ? BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                : BigInteger.Parse(text, CultureInfo.InvariantCulture);
            if (wide && suffix == 'u') return new(checked((ulong)integer), new ShaderType.Scalar(ScalarKind.Uint, 8)) { Span = span };
            if (wide && suffix == 'i') return new(checked((long)integer), new ShaderType.Scalar(ScalarKind.Sint, 8)) { Span = span };
            if (hasSuffix && suffix == 'u') return new(checked((uint)integer), ShaderType.U32) { Span = span };
            if (hasSuffix && suffix == 'i') return new(checked((int)integer), ShaderType.I32) { Span = span };
            return new(checked((long)integer), AbstractInt) { Span = span };
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new ShaderException(DiagnosticStage.WgslParse, $"Numeric literal '{source}' is malformed or out of range.", span);
        }
    }

    private static double HexFloat(string source)
    {
        string[] parts = source[2..].Split(['p', 'P']);
        int exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        int dot = parts[0].IndexOf('.');
        string digits = parts[0].Replace(".", "", StringComparison.Ordinal);
        if (dot >= 0) exponent = checked(exponent - 4 * (parts[0].Length - dot - 1));
        var mantissa = BigInteger.Parse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return double.ScaleB((double)mantissa, exponent);
    }
}

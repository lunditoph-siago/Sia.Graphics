using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Valid;

/// <summary>WGSL builtin constraints whose evaluation stage depends on only some arguments.</summary>
internal static class WgslBuiltinConstantRules
{
    // Return an offending argument: a source frontend rejects it, while native/
    // runtime target lowering must retain that argument's runtime evaluation.
    internal static int? InvalidArgument(string name, ShaderType type, Func<int, Expression?> constant)
    {
        double[]? Numbers(int index) => constant(index) switch {
            Expression.Literal literal => [Number(literal.Value)],
            Expression.Construct construct when construct.Components.All(c => c is Expression.Literal)
                => construct.Components.Cast<Expression.Literal>().Select(c => Number(c.Value)).ToArray(), _ => null
        };
        var scalar = type is ShaderType.Vector vector ? vector.Component : type as ShaderType.Scalar;
        if (name is "clamp" or "smoothstep") {
            int first = name == "clamp" ? 1 : 0;
            if (Numbers(first) is { } low && Numbers(first + 1) is { } high
                && low.Zip(high, (a, b) => name == "clamp" ? a > b : a == b).Any(b => b)) return first;
        }
        if (name == "ldexp" && Numbers(1) is { } exponent
            && exponent.Any(e => e > (scalar?.Width == 2 ? 16 : scalar?.Width == 8 ? 1024 : 128))) return 1;
        if (name is "extractBits" or "insertBits") {
            int first = name == "extractBits" ? 1 : 2;
            if (Numbers(first) is [var offset] && Numbers(first + 1) is [var count]
                && offset + count > scalar!.Width * 8) return first;
        }
        return null;
    }

    private static double Number(object value) => value switch {
        Half half => (double)half, System.Numerics.BigInteger integer => (double)integer,
        _ => System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
    };
}

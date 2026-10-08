namespace Sia.Spirv.Naga.IR;

internal static class OverrideExpressions
{
    private static readonly HashSet<string> PureBuiltins = new("abs acos acosh all any asin asinh atan atanh atan2 ceil clamp cos cosh countLeadingZeros countOneBits countTrailingZeros cross degrees determinant distance dot dot4I8Packed dot4U8Packed exp exp2 extractBits faceForward firstLeadingBit firstTrailingBit floor fma fract frexp insertBits inverseSqrt isInf isNan ldexp length log log2 max min mix modf normalize outerProduct pack2x16float pack2x16snorm pack2x16unorm pack4x8snorm pack4x8unorm pack4xI8 pack4xU8 pack4xI8Clamp pack4xU8Clamp pow quantizeToF16 radians reflect refract reverseBits round saturate select sign sin sinh smoothstep sqrt step tan tanh transpose trunc unpack2x16float unpack2x16snorm unpack2x16unorm unpack4x8snorm unpack4x8unorm unpack4xI8 unpack4xU8".Split(' '), StringComparer.Ordinal);

    public static bool IsValid(Expression expression, IReadOnlyDictionary<string, ShaderConstant> constants) => new Checker(constants).IsValid(expression);

    internal sealed class Checker(IReadOnlyDictionary<string, ShaderConstant> constants)
    {
        private readonly HashSet<string> active = new(StringComparer.Ordinal);
        private readonly Dictionary<Expression, bool> expressions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, bool> knownConstants = new(StringComparer.Ordinal);
        public bool IsValid(Expression expression) => Visit(expression, 0);
        private bool Visit(Expression value, int depth)
        {
            if (depth > 256) return false;
            if (expressions.TryGetValue(value, out bool known)) return known;
            bool Child(Expression e) => Visit(e, depth + 1);
            bool Reference(Expression.Reference reference)
            {
                if (knownConstants.TryGetValue(reference.Name, out bool valid)) return valid;
                if (!constants.TryGetValue(reference.Name, out var constant) || !active.Add(reference.Name)) return false;
                valid = constant.Value is null ? constant.IsOverride : Visit(constant.Value, 0);
                active.Remove(reference.Name); knownConstants.Add(reference.Name, valid); return valid;
            }
            bool result = value switch
            {
                Expression.Literal => true, Expression.Reference r => Reference(r),
                Expression.Unary u => Child(u.Operand), Expression.Binary b => Child(b.Left) && Child(b.Right),
                Expression.Construct c => c.Components.All(Child), Expression.Convert c => Child(c.Operand),
                Expression.Access a => Child(a.Base) && Child(a.Index), Expression.Member m => Child(m.Base),
                Expression.Swizzle s => Child(s.Vector), Expression.Select s => Child(s.Condition) && Child(s.Accept) && Child(s.Reject),
                Expression.Call c => PureBuiltins.Contains(c.Function) && c.Arguments.All(Child), _ => false
            };
            expressions[value] = result; return result;
        }
    }
}

using System.Numerics;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static partial class SpirvIntegerArithmeticLowering
{
    private static Expression Splat(Expression value, ShaderType type)
        => type is ShaderType.Vector vector ? new Expression.Construct(type, Enumerable.Repeat(value, vector.Size).ToArray()) : value;

    private static BigInteger[]? IntegerLanes(Expression? value) => value switch {
        Expression.Literal { Value: short n } => [n], Expression.Literal { Value: ushort n } => [n],
        Expression.Literal { Value: int n } => [n], Expression.Literal { Value: uint n } => [n],
        Expression.Literal { Value: long n } => [n], Expression.Literal { Value: ulong n } => [n],
        Expression.Construct { Type: ShaderType.Vector } vector when vector.Components.All(c => IntegerLanes(c) is { Length: 1 })
            => vector.Components.Select(c => IntegerLanes(c)![0]).ToArray(),
        _ => null
    };

    private static bool SafeDivisor(Expression? value, ShaderType.Scalar type)
        => IntegerLanes(value) is { Length: > 0 } lanes && lanes.All(n => n != 0 && (type.Kind != ScalarKind.Sint || n != -1));

    private static (string Operator, Expression Operand)? UnsignedReduction(Expression divisor, ShaderType type, string op)
    {
        var component = type is ShaderType.Vector v ? v.Component : (ShaderType.Scalar)type;
        if (component.Kind != ScalarKind.Uint || IntegerLanes(divisor) is not { Length: > 0 } lanes
            || lanes.Any(n => n <= 0 || (n & (n - 1)) != 0)) return null;
        ShaderType operandType = op == "/" && type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.U32)
            : op == "/" ? ShaderType.U32 : type;
        var operandComponent = operandType is ShaderType.Vector ov ? ov.Component : (ShaderType.Scalar)operandType;
        Expression Lane(BigInteger n) {
            if (op == "/") { uint shift = 0; for (; n > 1; n >>= 1) shift++; return Expression.U32(shift); }
            object mask = operandComponent.Width switch { 2 => (object)(ushort)(n - 1), 8 => (ulong)(n - 1), _ => (uint)(n - 1) };
            return new Expression.Literal(mask, operandComponent);
        }
        var values = lanes.Select(Lane).ToArray();
        Expression operand = operandType is ShaderType.Vector
            ? new Expression.Construct(operandType, values.Length == 1 ? Enumerable.Repeat(values[0], ((ShaderType.Vector)operandType).Size).ToArray() : values)
            : values[0];
        return (op == "/" ? ">>" : "&", operand);
    }

    private static bool BoundedShift(Expression value, int bits)
    {
        if (ConstantEvaluator.TryEvaluateRuntime(value, out var constant) && IntegerLanes(constant) is { Length: > 0 } lanes)
            return lanes.All(n => n >= 0 && n < bits);
        return value is Expression.Binary { Operator: "&" } mask && (BoundedShift(mask.Left, bits) || BoundedShift(mask.Right, bits));
    }

    // Prove only concrete values. Override defaults and block parameters remain
    // unknown; no memory reads or calls are evaluated or moved by these facts.
    private sealed class IntegerFacts(ControlFlowFunction graph, Module module)
    {
        private readonly Dictionary<int, ValueOperation> definitions = graph.Blocks.SelectMany(b => b.Instructions)
            .Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
        private readonly Dictionary<int, Expression?> known = [];
        private readonly Dictionary<string, ShaderConstant> constants = module.Constants
            .Where(c => !c.IsOverride && !c.IsSpecialization && !graph.Signature.Arguments.Any(a => a.Name == c.Name))
            .ToDictionary(c => c.Name, StringComparer.Ordinal);
        private readonly HashSet<string> resolving = new(StringComparer.Ordinal);

        private Expression? Named(Expression expression)
        {
            if (expression is Expression.Reference reference) {
                if (!constants.TryGetValue(reference.Name, out var constant) || constant.Value is null || !resolving.Add(reference.Name)) return null;
                try { return Named(constant.Value); } finally { resolving.Remove(reference.Name); }
            }
            Expression? mapped = expression switch {
                Expression.Literal => expression,
                Expression.Construct c when c.Components.All(c => Named(c) is not null) => c with { Components = c.Components.Select(c => Named(c)!).ToArray() },
                Expression.Unary u when Named(u.Operand) is { } operand => u with { Operand = operand },
                Expression.Binary b when Named(b.Left) is { } left && Named(b.Right) is { } right => b with { Left = left, Right = right },
                Expression.Convert c when Named(c.Operand) is { } operand => c with { Operand = operand }, _ => null
            };
            return mapped is not null && ConstantEvaluator.TryEvaluateRuntime(mapped, out var value) ? value : null;
        }

        internal Expression? Constant(SsaValue value)
        {
            if (known.TryGetValue(value.Id, out var cached)) return cached;
            known.Add(value.Id, null);
            Expression? expression = definitions.GetValueOrDefault(value.Id) switch {
                ValueOperation.Literal literal => new Expression.Literal(literal.Value, value.Type),
                ValueOperation.Let let => Constant(let.Value),
                ValueOperation.Symbol symbol => Named(new Expression.Reference(symbol.Name, value.Type)),
                ValueOperation.Construct c when c.Components.All(c => Constant(c) is not null)
                    => new Expression.Construct(value.Type, c.Components.Select(c => Constant(c)!).ToArray()),
                ValueOperation.Convert c when Constant(c.Operand) is { } operand => new Expression.Convert(value.Type, operand, c.Bitcast),
                ValueOperation.Unary u when Constant(u.Operand) is { } operand => new Expression.Unary(u.Operator, operand, value.Type),
                ValueOperation.Binary b when Constant(b.Left) is { } left && Constant(b.Right) is { } right => new Expression.Binary(b.Operator, left, right, value.Type),
                _ => null
            };
            return known[value.Id] = expression is not null && ConstantEvaluator.TryEvaluateRuntime(expression, out var folded) ? folded : null;
        }

        internal bool BoundedShift(SsaValue value, int bits)
        {
            if (Constant(value) is { } constant && SpirvIntegerArithmeticLowering.BoundedShift(constant, bits)) return true;
            return definitions.GetValueOrDefault(value.Id) switch {
                ValueOperation.Let let => BoundedShift(let.Value, bits),
                ValueOperation.Convert convert when value.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint } target
                    && convert.Operand.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint }
                    && new BigInteger(bits - 1) < (BigInteger.One << (target.Width * 8 - (target.Kind == ScalarKind.Sint ? 1 : 0)))
                    => BoundedShift(convert.Operand, bits),
                ValueOperation.Binary { Operator: "&" } mask => BoundedShift(mask.Left, bits) || BoundedShift(mask.Right, bits), _ => false
            };
        }

        internal bool RedundantMask(SsaValue value, SsaValue mask)
        {
            if (IntegerLanes(Constant(mask)) is not { Length: > 0 } lanes || lanes.Any(n => n != lanes[0])) return false;
            var maximum = lanes[0];
            return maximum >= 0 && maximum < int.MaxValue && (maximum & (maximum + 1)) == 0
                && BoundedShift(value, (int)maximum + 1);
        }
    }
}

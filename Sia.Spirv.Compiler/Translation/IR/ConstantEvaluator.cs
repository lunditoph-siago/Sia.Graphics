using System.Globalization;
using System.Numerics;

namespace Sia.Spirv.Compiler.Translation.IR;

public static class ConstantEvaluator
{
    private sealed class NotConstant : Exception;
    public static bool TryEvaluate(Expression expression, out Expression value)
    {
        try { value = EvaluateCore(expression); return true; }
        catch (NotConstant) { value = expression; return false; }
    }
    // Runtime integer arithmetic wraps and division uses backend guards. A failed
    // optional fold must not turn a runtime operation into a constant-stage error.
    internal static bool TryEvaluateRuntime(Expression expression, out Expression value)
    {
        try { value = Eval(expression); return true; }
        catch (Exception e) when (e is NotConstant or ArithmeticException) { value = expression; return false; }
    }
    public static Expression Evaluate(Expression expression)
    {
        try { return EvaluateCore(expression); }
        catch (NotConstant) { throw new ShaderException(DiagnosticStage.Validation, "Expression is not constant.", expression.Span); }
    }
    private static Expression EvaluateCore(Expression expression)
    {
        try { return Eval(expression); }
        catch (Exception e) when (e is OverflowException or DivideByZeroException or ArithmeticException)
        {
            throw new ShaderException(DiagnosticStage.Validation, "Constant expression is outside its type range or divides by zero.", expression.Span);
        }
    }

    private static Expression Eval(Expression expression) => expression switch
    {
        Expression.Literal => expression,
        Expression.Construct c => Construct(c),
        Expression.Unary u => Unary(u.Operator, Eval(u.Operand), u.Type),
        Expression.Binary { Operator: "&&" or "||" } b => ShortCircuit(b),
        Expression.Binary b => Binary(b.Operator, Eval(b.Left), Eval(b.Right), b.Type),
        Expression.Convert c => ConvertCore(Eval(c.Operand), c.Type, c.Bitcast),
        Expression.Select s => Select(Eval(s.Condition), Eval(s.Accept), Eval(s.Reject)),
        Expression.Access a => Access(Eval(a.Base), Eval(a.Index)),
        Expression.Member m => Member(Eval(m.Base), m.Name),
        Expression.Swizzle s => Swizzle(Eval(s.Vector), s.Components, s.Type),
        Expression.Call c when c.Binding != CallBinding.Function => Call(c.Function, c.Arguments.Select(Eval).ToArray(), c.Type),
        _ => throw new NotConstant()
    };

    private static Expression ShortCircuit(Expression.Binary binary)
    {
        var left = Eval(binary.Left);
        if (left is Expression.Literal { Value: bool value } && (binary.Operator == "&&" && !value || binary.Operator == "||" && value)) return left;
        return Binary(binary.Operator, left, Eval(binary.Right), binary.Type);
    }

    private static Expression Construct(Expression.Construct construct)
    {
        Expression[] elements = construct.Components.Select(Eval).ToArray();
        if (construct.Type is ShaderType.Scalar scalar)
        {
            if (elements.Length == 0) return Zero(scalar);
            if (elements.Length == 1) return ConvertCore(elements[0], scalar, false);
        }
        if (construct.Type is ShaderType.Vector vector)
        {
            elements = elements.SelectMany(e => e is Expression.Construct { Type: ShaderType.Vector } c ? c.Components : [e]).ToArray();
            if (elements.Length == 0) elements = Enumerable.Repeat(Zero(vector.Component), vector.Size).ToArray();
            if (elements.Length == 1) elements = Enumerable.Repeat(elements[0], vector.Size).ToArray();
            if (elements.Length != vector.Size) throw new ShaderException(DiagnosticStage.Validation, "Wrong constant vector component count.");
        }
        if (elements.Length == 0)
            elements = construct.Type switch
            {
                ShaderType.Matrix m => Enumerable.Repeat(Construct(new(new ShaderType.Vector(m.Rows, m.Component), [])), m.Columns).ToArray(),
                ShaderType.Array { Length: uint n } a => Enumerable.Repeat(ZeroValue(a.Element), checked((int)n)).ToArray(),
                ShaderType.Structure s => s.Members.Select(m => ZeroValue(m.Type)).ToArray(),
                _ => elements
            };
        return new Expression.Construct(construct.Type, elements);
    }
    private static Expression ZeroValue(ShaderType type) => type is ShaderType.Scalar scalar ? Zero(scalar) : Construct(new(type, []));

    private static Expression Zero(ShaderType.Scalar type) => type.Kind switch
    {
        ScalarKind.Bool => new Expression.Literal(false, type),
        ScalarKind.Float or ScalarKind.AbstractFloat => Scalar(0.0, type),
        _ => Scalar(BigInteger.Zero, type)
    };
    private static double Floating(object value) => value is Half h ? (double)h : value is bool b ? b ? 1 : 0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
    private static BigInteger Integer(object value) => value switch
    {
        short i => i, ushort u => u, int i => i, uint u => u, long l => l, ulong u => u, BigInteger b => b, bool b => b ? 1 : 0,
        _ => new BigInteger(Floating(value))
    };
    private static Expression Scalar(BigInteger value, ShaderType type) => type is ShaderType.Scalar scalar
        ? new Expression.Literal((scalar.Kind, scalar.Width) switch
        {
            (ScalarKind.Sint, 2) => (object)checked((short)value), (ScalarKind.Uint, 2) => checked((ushort)value),
            (ScalarKind.Sint, 4) => checked((int)value), (ScalarKind.Uint, 4) => checked((uint)value),
            (ScalarKind.Sint, 8) or (ScalarKind.AbstractInt, 8) => checked((long)value), (ScalarKind.Uint, 8) => checked((ulong)value),
            _ => throw new NotConstant()
        }, type) : throw new NotConstant();
    private static Expression Scalar(double value, ShaderType type)
    {
        if (!double.IsFinite(value)) throw new ArithmeticException();
        object result = type switch
        {
            ShaderType.Scalar { Kind: ScalarKind.Float, Width: 2 } => (object)(Half)value,
            ShaderType.Scalar { Kind: ScalarKind.Float, Width: 4 } => (float)value,
            ShaderType.Scalar { Kind: ScalarKind.Float or ScalarKind.AbstractFloat, Width: 8 } => value,
            _ => throw new NotConstant()
        };
        if (result is Half h && !Half.IsFinite(h) || result is float f && !float.IsFinite(f)) throw new ArithmeticException();
        return new Expression.Literal(result, type);
    }

    public static Expression Convert(Expression input, ShaderType type, bool bitcast = false)
    {
        try { return ConvertCore(input, type, bitcast); }
        catch (NotConstant) { throw new ShaderException(DiagnosticStage.Validation, "Unsupported constant conversion.", input.Span); }
        catch (Exception e) when (e is ArithmeticException)
        { throw new ShaderException(DiagnosticStage.Validation, "Constant conversion is outside its type range.", input.Span); }
    }

    private static Expression ConvertCore(Expression input, ShaderType type, bool bitcast = false)
    {
        if (bitcast) return Bitcast(input, type);
        if (input is Expression.Construct c && type is ShaderType.Vector vector)
            return new Expression.Construct(type, c.Components.Select(e => ConvertCore(e, vector.Component, bitcast)).ToArray());
        if (input is Expression.Construct matrix && type is ShaderType.Matrix m && !bitcast)
            return new Expression.Construct(type, matrix.Components.Select(e => ConvertCore(e, new ShaderType.Vector(m.Rows, m.Component))).ToArray());
        if (input is not Expression.Literal literal || type is not ShaderType.Scalar scalar) throw new NotConstant();
        if (scalar.Kind is ScalarKind.Sint or ScalarKind.Uint && literal.Type is ShaderType.Scalar source)
        {
            if (source.Kind is ScalarKind.Float or ScalarKind.AbstractFloat)
            {
                var bounds = NumericConversions.IntegerFloatBounds(source, scalar);
                double value = Floating(literal.Value);
                return Scalar(new BigInteger(double.IsNaN(value) ? 0 : Math.Clamp(value, bounds.Minimum, bounds.Maximum)), type);
            }
            if (source.Kind is ScalarKind.Sint or ScalarKind.Uint)
            {
                int bits = scalar.Width * 8;
                BigInteger value = Integer(literal.Value) & ((BigInteger.One << bits) - 1);
                if (scalar.Kind == ScalarKind.Sint && value >= (BigInteger.One << (bits - 1))) value -= BigInteger.One << bits;
                return Scalar(value, type);
            }
        }
        return scalar.Kind switch
        {
            ScalarKind.Bool => new Expression.Literal(literal.Value is bool boolean ? boolean : Floating(literal.Value) != 0, type),
            ScalarKind.Float or ScalarKind.AbstractFloat => Scalar(Floating(literal.Value), type),
            ScalarKind.Sint or ScalarKind.Uint or ScalarKind.AbstractInt => Scalar(Integer(literal.Value), type),
            _ => throw new NotConstant()
        };
    }

    private static Expression Bitcast(Expression input, ShaderType target)
    {
        ShaderType.Scalar Component(ShaderType t) => t switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, _ => throw new NotConstant() };
        var from = Component(input.Type); var to = Component(target);
        int count = target is ShaderType.Vector vector ? vector.Size : 1;
        Expression[] inputs = input is Expression.Construct c ? c.Components.ToArray() : [input];
        if (inputs.Length * from.Width != count * to.Width) throw new NotConstant();
        BigInteger packed = BigInteger.Zero;
        for (int i = 0; i < inputs.Length; i++)
        {
            if (inputs[i] is not Expression.Literal value) throw new NotConstant();
            ulong bits = value.Value switch
            {
                Half h => BitConverter.HalfToUInt16Bits(h), float f => BitConverter.SingleToUInt32Bits(f), double d => BitConverter.DoubleToUInt64Bits(d),
                ushort u => u, short n => unchecked((ushort)n), uint u => u, int n => unchecked((uint)n), ulong u => u, long n => unchecked((ulong)n), _ => throw new NotConstant()
            };
            packed |= new BigInteger(bits) << (i * from.Width * 8);
        }
        Expression[] outputs = new Expression[count];
        for (int i = 0; i < count; i++)
        {
            ulong bits = (ulong)(packed >> (i * to.Width * 8) & ((BigInteger.One << (to.Width * 8)) - 1));
            object value = (to.Kind, to.Width) switch
            {
                (ScalarKind.Float, 2) => (object)BitConverter.UInt16BitsToHalf((ushort)bits), (ScalarKind.Float, 4) => BitConverter.UInt32BitsToSingle((uint)bits),
                (ScalarKind.Float, 8) => BitConverter.UInt64BitsToDouble(bits), (ScalarKind.Uint, 2) => (ushort)bits, (ScalarKind.Sint, 2) => unchecked((short)bits),
                (ScalarKind.Uint, 4) => (uint)bits, (ScalarKind.Sint, 4) => unchecked((int)bits),
                (ScalarKind.Uint, 8) => bits, (ScalarKind.Sint, 8) => unchecked((long)bits), _ => throw new NotConstant()
            };
            outputs[i] = new Expression.Literal(value, to);
        }
        return target is ShaderType.Vector ? new Expression.Construct(target, outputs) : outputs[0];
    }

    private static Expression Unary(string op, Expression input, ShaderType type)
    {
        if (input is Expression.Construct c && type is ShaderType.Vector v)
            return new Expression.Construct(type, c.Components.Select(e => Unary(op, e, v.Component)).ToArray());
        if (input is not Expression.Literal literal) throw new NotConstant();
        if (op == "!" && literal.Value is bool b) return Expression.Bool(!b);
        if (type is ShaderType.Scalar { Kind: ScalarKind.Float or ScalarKind.AbstractFloat })
            return op == "-" ? Scalar(-Floating(literal.Value), type) : throw new NotConstant();
        BigInteger value = Integer(literal.Value);
        if (op == "~")
        {
            value = ~value;
            if (type is ShaderType.Scalar { Kind: ScalarKind.Uint } s) value &= (BigInteger.One << (s.Width * 8)) - 1;
        }
        else if (op == "-") value = -value;
        else throw new NotConstant();
        return Scalar(value, type);
    }

    private static Expression Binary(string op, Expression left, Expression right, ShaderType type)
    {
        if (left is Expression.Construct lc || right is Expression.Construct)
        {
            var l = (left as Expression.Construct)?.Components;
            var r = (right as Expression.Construct)?.Components;
            int count = l?.Count ?? r!.Count;
            if (type is not ShaderType.Vector v) throw new NotConstant();
            return new Expression.Construct(type, Enumerable.Range(0, count).Select(i => Binary(op, l?[i] ?? left, r?[i] ?? right, v.Component)).ToArray());
        }
        if (left is not Expression.Literal a || right is not Expression.Literal b) throw new NotConstant();
        if (a.Value is bool av && b.Value is bool bv) return Expression.Bool(op switch
        {
            "&&" or "&" => av & bv, "||" or "|" => av | bv, "^" => av ^ bv, "==" => av == bv, "!=" => av != bv, _ => throw new NotConstant()
        });
        bool floating = a.Type is ShaderType.Scalar { Kind: ScalarKind.Float or ScalarKind.AbstractFloat };
        if (floating)
        {
            double x = Floating(a.Value), y = Floating(b.Value);
            if (op is "<" or ">" or "<=" or ">=" or "==" or "!=") return Expression.Bool(op switch
            { "<" => x < y, ">" => x > y, "<=" => x <= y, ">=" => x >= y, "==" => x == y, _ => x != y });
            return Scalar(op switch { "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, "%" => x % y, _ => throw new NotConstant() }, type);
        }
        BigInteger xi = Integer(a.Value), yi = Integer(b.Value);
        if (op is "<" or ">" or "<=" or ">=" or "==" or "!=") return Expression.Bool(op switch
        { "<" => xi < yi, ">" => xi > yi, "<=" => xi <= yi, ">=" => xi >= yi, "==" => xi == yi, _ => xi != yi });
        if (op is "<<" or ">>" && (yi < 0 || yi >= (a.Type is ShaderType.Scalar s ? s.Width * 8 : 64))) throw new ArithmeticException();
        return Scalar(op switch
        {
            "+" => xi + yi, "-" => xi - yi, "*" => xi * yi, "/" => xi / yi, "%" => xi % yi,
            "&" => xi & yi, "|" => xi | yi, "^" => xi ^ yi, "<<" => xi << (int)yi, ">>" => xi >> (int)yi, _ => throw new NotConstant()
        }, type);
    }

    private static Expression Select(Expression condition, Expression accept, Expression reject)
    {
        if (condition is Expression.Literal { Value: bool b }) return b ? accept : reject;
        if (condition is Expression.Construct c && accept is Expression.Construct a && reject is Expression.Construct r)
            return new Expression.Construct(a.Type, c.Components.Select((e, i) => Select(e, a.Components[i], r.Components[i])).ToArray());
        throw new NotConstant();
    }
    private static Expression Access(Expression container, Expression subscript)
    {
        if (container is not Expression.Construct c || subscript is not Expression.Literal i) throw new NotConstant();
        int index = checked((int)Integer(i.Value));
        if (index < 0 || index >= c.Components.Count) throw new ShaderException(DiagnosticStage.Validation, "Constant index is out of bounds.");
        return c.Components[index];
    }
    private static Expression Member(Expression container, string name)
    {
        if (container is not Expression.Construct { Type: ShaderType.Structure s } c) throw new NotConstant();
        int index = s.Members.ToList().FindIndex(m => m.Name == name);
        return index >= 0 && index < c.Components.Count ? c.Components[index] : throw new NotConstant();
    }

    private static Expression Swizzle(Expression input, string components, ShaderType type)
    {
        if (input is not Expression.Construct c) throw new NotConstant();
        return new Expression.Construct(type, components.Select(ch => c.Components["xyzw".IndexOf(ch) is int i && i >= 0 ? i : "rgba".IndexOf(ch)]).ToArray());
    }

    private static Expression Call(string name, Expression[] arguments, ShaderType type)
    {
        if (name is "pack4xI8" or "pack4xU8" or "pack4xI8Clamp" or "pack4xU8Clamp" && arguments is [Expression.Construct packedVector] && packedVector.Components.All(e => e is Expression.Literal))
        {
            BigInteger packed = BigInteger.Zero;
            for (int i = 0; i < 4; i++)
            {
                BigInteger component = Integer(((Expression.Literal)packedVector.Components[i]).Value);
                if (name.EndsWith("Clamp", StringComparison.Ordinal)) component = BigInteger.Min(BigInteger.Max(component, name.Contains("I8", StringComparison.Ordinal) ? -128 : 0), name.Contains("I8", StringComparison.Ordinal) ? 127 : 255);
                packed |= (component & 255) << (i * 8);
            }
            return Scalar(packed, type);
        }
        if (name is "unpack4xI8" or "unpack4xU8" or "dot4I8Packed" or "dot4U8Packed" && arguments.All(a => a is Expression.Literal))
        {
            bool signed = name.Contains("I8", StringComparison.Ordinal);
            BigInteger Component(Expression argument, int i)
            {
                BigInteger value = Integer(((Expression.Literal)argument).Value) >> (8 * i) & 255;
                return signed && value >= 128 ? value - 256 : value;
            }
            if (name.StartsWith("dot", StringComparison.Ordinal))
                return Scalar(Enumerable.Range(0, 4).Aggregate(BigInteger.Zero, (sum, i) => sum + Component(arguments[0], i) * Component(arguments[1], i)), type);
            return new Expression.Construct(type, Enumerable.Range(0, 4).Select(i => Scalar(Component(arguments[0], i), ((ShaderType.Vector)type).Component)).ToArray());
        }
        if (name == "select" && arguments.Length == 3) return Select(arguments[2], arguments[1], arguments[0]);
        if (name is "all" or "any" && arguments is [Expression.Construct c] && c.Components.All(e => e is Expression.Literal { Value: bool }))
            return Expression.Bool(name == "all" ? c.Components.All(e => ((Expression.Literal)e).Value is true) : c.Components.Any(e => ((Expression.Literal)e).Value is true));
        if (name is "dot" or "cross" or "length" or "distance" or "normalize" && arguments[0] is Expression.Construct { Type: ShaderType.Vector } vector)
        {
            var component = ((ShaderType.Vector)vector.Type).Component;
            if (name == "cross" && arguments[1] is Expression.Construct crossRight)
            {
                Expression Part(int a, int b) => Binary("-", Binary("*", vector.Components[a], crossRight.Components[b], component), Binary("*", vector.Components[b], crossRight.Components[a], component), component);
                return new Expression.Construct(type, [Part(1, 2), Part(2, 0), Part(0, 1)]);
            }
            Expression sum = Zero(component);
            for (int i = 0; i < vector.Components.Count; i++)
            {
                Expression left = vector.Components[i], right = name == "dot" && arguments[1] is Expression.Construct other ? other.Components[i] : left;
                if (name == "distance" && arguments[1] is Expression.Construct end) left = right = Binary("-", left, end.Components[i], component);
                sum = Binary("+", sum, Binary("*", left, right, component), component);
            }
            if (name == "dot") return sum;
            Expression length = Call("sqrt", [sum], component);
            return name == "normalize" ? new Expression.Construct(type, vector.Components.Select(e => Binary("/", e, length, component)).ToArray()) : length;
        }
        if (type is ShaderType.Vector resultVector && arguments.Any(a => a is Expression.Construct { Type: ShaderType.Vector }))
            return new Expression.Construct(type, Enumerable.Range(0, resultVector.Size).Select(i => Call(name, arguments.Select(a => a is Expression.Construct { Type: ShaderType.Vector } v ? v.Components[i] : a).ToArray(), resultVector.Component)).ToArray());
        if (arguments.Any(e => e is not Expression.Literal)) throw new NotConstant();
        if (name is "extractBits" or "insertBits" && type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint } field)
        {
            bool insert = name == "insertBits"; int first = insert ? 2 : 1, width = field.Width * 8;
            int offset = (int)BigInteger.Min(Integer(((Expression.Literal)arguments[first]).Value), width);
            int count = (int)BigInteger.Min(Integer(((Expression.Literal)arguments[first + 1]).Value), width - offset);
            BigInteger full = (BigInteger.One << width) - 1, mask = (BigInteger.One << count) - 1;
            BigInteger value = Integer(((Expression.Literal)arguments[0]).Value) & full;
            if (insert) value = (value & ~(mask << offset)) | ((Integer(((Expression.Literal)arguments[1]).Value) & mask) << offset);
            else
            {
                value = value >> offset & mask;
                if (field.Kind == ScalarKind.Sint && count > 0 && (value & (BigInteger.One << (count - 1))) != 0) value -= BigInteger.One << count;
            }
            if (insert && field.Kind == ScalarKind.Sint && value >= (BigInteger.One << (width - 1))) value -= BigInteger.One << width;
            return Scalar(value, type);
        }
        if (arguments.Length == 1)
        {
            var a = (Expression.Literal)arguments[0];
            if (name is "isNan" or "isInf") return Expression.Bool(name == "isNan" ? double.IsNaN(Floating(a.Value)) : double.IsInfinity(Floating(a.Value)));
            if (name is "countLeadingZeros" or "countOneBits" or "countTrailingZeros" or "firstLeadingBit" or "firstTrailingBit" or "reverseBits")
            {
                int width = ((ShaderType.Scalar)a.Type).Width * 8;
                ulong mask = width == 64 ? ulong.MaxValue : (1ul << width) - 1;
                ulong bits = (ulong)(Integer(a.Value) & mask);
                ulong leading = a.Type is ShaderType.Scalar { Kind: ScalarKind.Sint } && (bits & (1ul << (width - 1))) != 0 ? ~bits & mask : bits;
                ulong reverse = 0;
                for (int i = 0; i < width; i++) reverse = reverse << 1 | (bits >> i & 1);
                int LeadingZeros(ulong value) => BitOperations.LeadingZeroCount(value) - (64 - width);
                ulong result = name switch
                {
                    "countLeadingZeros" => (ulong)LeadingZeros(bits), "countTrailingZeros" => bits == 0 ? (ulong)width : (ulong)BitOperations.TrailingZeroCount(bits),
                    "countOneBits" => (ulong)BitOperations.PopCount(bits), "firstLeadingBit" => leading == 0 ? mask : (ulong)(width - 1 - LeadingZeros(leading)),
                    "firstTrailingBit" => bits == 0 ? mask : (ulong)BitOperations.TrailingZeroCount(bits), _ => reverse
                };
                BigInteger integer = result;
                if (type is ShaderType.Scalar { Kind: ScalarKind.Sint } && (result & (1ul << (width - 1))) != 0) integer -= BigInteger.One << width;
                return Scalar(integer, type);
            }
            if (name is "abs" or "sign" && a.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint or ScalarKind.AbstractInt }) return Scalar(name == "abs" ? BigInteger.Abs(Integer(a.Value)) : new BigInteger(Integer(a.Value).Sign), type);
            double x = Floating(a.Value);
            return Scalar(name switch
            {
                "abs" => System.Math.Abs(x), "sign" => System.Math.Sign(x), "floor" => System.Math.Floor(x), "ceil" => System.Math.Ceiling(x),
                "round" => System.Math.Round(x, MidpointRounding.ToEven), "trunc" => System.Math.Truncate(x), "fract" => x - System.Math.Floor(x),
                "sqrt" => System.Math.Sqrt(x), "inverseSqrt" => 1 / System.Math.Sqrt(x), "sin" => System.Math.Sin(x), "cos" => System.Math.Cos(x), "tan" => System.Math.Tan(x),
                "asin" => System.Math.Asin(x), "acos" => System.Math.Acos(x), "atan" => System.Math.Atan(x), "exp" => System.Math.Exp(x), "log" => System.Math.Log(x),
                "exp2" => System.Math.Pow(2, x), "log2" => System.Math.Log2(x), "radians" => x * System.Math.PI / 180, "degrees" => x * 180 / System.Math.PI,
                "sinh" => System.Math.Sinh(x), "cosh" => System.Math.Cosh(x), "tanh" => System.Math.Tanh(x), "asinh" => System.Math.Asinh(x), "acosh" => System.Math.Acosh(x), "atanh" => System.Math.Atanh(x), "saturate" => System.Math.Clamp(x, 0, 1),
                "quantizeToF16" => (double)(Half)x,
                _ => throw new NotConstant()
            }, type);
        }
        if (name is "min" or "max" or "clamp")
        {
            if (type is ShaderType.Scalar { Kind: ScalarKind.Float or ScalarKind.AbstractFloat })
            {
                double a = Floating(((Expression.Literal)arguments[0]).Value), b = Floating(((Expression.Literal)arguments[1]).Value);
                return Scalar(name == "min" ? System.Math.Min(a, b) : name == "max" ? System.Math.Max(a, b) : System.Math.Clamp(a, b, Floating(((Expression.Literal)arguments[2]).Value)), type);
            }
            BigInteger x = Integer(((Expression.Literal)arguments[0]).Value), y = Integer(((Expression.Literal)arguments[1]).Value);
            return Scalar(name == "min" ? BigInteger.Min(x, y) : name == "max" ? BigInteger.Max(x, y) : BigInteger.Min(BigInteger.Max(x, y), Integer(((Expression.Literal)arguments[2]).Value)), type);
        }
        if (arguments.Length is 2 or 3 && type is ShaderType.Scalar { Kind: ScalarKind.Float or ScalarKind.AbstractFloat })
        {
            double x = Floating(((Expression.Literal)arguments[0]).Value), y = Floating(((Expression.Literal)arguments[1]).Value), z = arguments.Length == 3 ? Floating(((Expression.Literal)arguments[2]).Value) : 0;
            double Smooth() { double t = System.Math.Clamp((z - x) / (y - x), 0, 1); return t * t * (3 - 2 * t); }
            return Scalar(name switch
            {
                "pow" => System.Math.Pow(x, y), "atan2" => System.Math.Atan2(x, y), "step" => y < x ? 0 : 1,
                "fma" => System.Math.FusedMultiplyAdd(x, y, z), "mix" => x * (1 - z) + y * z, "smoothstep" => Smooth(), "ldexp" => System.Math.ScaleB(x, checked((int)y)),
                _ => throw new NotConstant()
            }, type);
        }
        throw new NotConstant();
    }
}

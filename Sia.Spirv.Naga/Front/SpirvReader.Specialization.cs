using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private Expression CaptureSpecialization(uint id, Expression value)
        {
            if (ConstantEvaluator.TryEvaluateRuntime(value, out var folded)) return folded;
            int component = 0;
            Expression Capture(Expression expression)
            {
                if (expression.Type is ShaderType.Scalar)
                {
                    string name = Name(id, "c");
                    if (value.Type is not ShaderType.Scalar) name += "_" + component++;
                    module.Constants.Add(new(name, expression.Type, expression) { IsSpecialization = true });
                    return new Expression.Reference(name, expression.Type);
                }
                uint count = expression.Type switch
                {
                    ShaderType.Vector v => (uint)v.Size, ShaderType.Matrix m => (uint)m.Columns,
                    ShaderType.Structure s => (uint)s.Members.Count, ShaderType.Array { Length: uint n } => n,
                    _ => throw Error("Unsupported specialization composite type.")
                };
                var parts = new List<Expression>();
                for (uint i = 0; i < count; i++) parts.Add(Capture(SpecIndex(expression, i)));
                return new Expression.Construct(expression.Type, parts);
            }
            return Capture(value);
        }

        private void ReadArrayType(uint id, uint element, uint lengthId)
        {
            Expression length = Value(lengthId);
            if (length.Type is not ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint })
                throw Error("Array length requires a scalar integer.");
            if (ConstantEvaluator.TryEvaluate(length, out var folded) && folded is Expression.Literal literal)
            {
                uint count;
                try { count = System.Convert.ToUInt32(literal.Value, System.Globalization.CultureInfo.InvariantCulture); }
                catch (OverflowException) { throw Error("Array length must be positive and fit in u32."); }
                if (count == 0) throw Error("Array length must be positive.");
                AddResourceArrayType(id, element, count); return;
            }
            if (!OverrideExpressions.IsValid(length, module.Constants.ToDictionary(c => c.Name, StringComparer.Ordinal)))
                throw Error("Array length requires a constant or specialization expression.");
            string name;
            if (length is Expression.Reference reference) name = reference.Name;
            else
            {
                name = Name(lengthId, "c");
                module.Constants.Add(new(name, length.Type, length) { IsSpecialization = true });
                values[lengthId] = new Expression.Reference(name, length.Type);
            }
            AddResourceArrayType(id, element, null, name);
        }

        private Expression SpecializationOperation()
        {
            var a = current.Operands; var op = (Op)a[2]; ShaderType type = Type(a[0]);
            Expression V(int index) => Value(a[index + 3]);
            void Arity(int count) => Count(count + 3, count + 3);
            Expression result;
            switch (op)
            {
                case Op.SConvert: case Op.UConvert:
                    Arity(1);
                    var inputType = SpecIntegerType(V(0).Type, op == Op.SConvert);
                    var outputType = SpecIntegerType(type, op == Op.SConvert);
                    if (SpecComponent(inputType).Width == SpecComponent(outputType).Width
                        || (inputType is ShaderType.Vector cv ? cv.Size : 1) != (outputType is ShaderType.Vector ov ? ov.Size : 1)) throw Error("Invalid specialization integer conversion shape or width.");
                    if (op == Op.UConvert) SpecSame(type, SpecIntegerType(type, false));
                    result = SpecCast(type, SpecCast(outputType, SpecCast(inputType, V(0)))); break;
                case Op.FConvert:
                    Arity(1);
                    if (SpecComponent(type).Kind != ScalarKind.Float || SpecComponent(V(0).Type).Kind != ScalarKind.Float
                        || SpecComponent(type).Width == SpecComponent(V(0).Type).Width
                        || (type is ShaderType.Vector fv ? fv.Size : 1) != (V(0).Type is ShaderType.Vector iv ? iv.Size : 1)) throw Error("Invalid specialization float conversion.");
                    result = new Expression.Convert(type, V(0)); break;
                case Op.QuantizeToF16:
                    Arity(1); SpecSame(type, V(0).Type); SpecSame(ShaderType.F32, SpecComponent(type));
                    result = new Expression.Call("quantizeToF16", [V(0)], type); break;
                case Op.SNegate:
                    Arity(1); SpecIntegerShape(type, V(0).Type);
                    result = SpecWrap(op, V(0), null, type); break;
                case Op.Not:
                    Arity(1); SpecIntegerShape(type, V(0).Type);
                    result = new Expression.Unary("~", SpecCast(type, V(0)), type); break;
                case Op.LogicalNot:
                    Arity(1); SpecSame(type, SpecBool(type)); SpecSame(type, V(0).Type);
                    result = new Expression.Unary("!", V(0), type); break;
                case Op.IAdd: case Op.ISub: case Op.IMul:
                    Arity(2); SpecIntegerShape(type, V(0).Type); SpecIntegerShape(type, V(1).Type);
                    result = SpecWrap(op, V(0), V(1), type); break;
                case Op.UDiv: case Op.SDiv: case Op.UMod: case Op.SRem: case Op.SMod:
                    Arity(2); SpecIntegerShape(type, V(0).Type); SpecIntegerShape(type, V(1).Type);
                    if (op is Op.UDiv or Op.UMod) { SpecSame(type, SpecIntegerType(type, false)); SpecSame(type, V(0).Type); SpecSame(type, V(1).Type); }
                    var arithmetic = SpecIntegerType(type, op is Op.SDiv or Op.SRem or Op.SMod);
                    var left = SpecCast(arithmetic, V(0)); var right = SpecCast(arithmetic, V(1));
                    Expression remainder = new Expression.Binary(op is Op.UDiv or Op.SDiv ? "/" : "%", left, right, arithmetic);
                    if (op == Op.SMod)
                    {
                        var zero = SpecUnsigned(arithmetic, 0, true);
                        var differentSigns = new Expression.Binary("!=", new Expression.Binary("<", left, zero, SpecBool(arithmetic)), new Expression.Binary("<", right, zero, SpecBool(arithmetic)), SpecBool(arithmetic));
                        var nonzero = new Expression.Binary("!=", remainder, zero, SpecBool(arithmetic));
                        // Only add the divisor for opposite signs and nonzero remainder.
                        // Gating the operand also keeps eager constant evaluation in range.
                        var condition = new Expression.Select(nonzero, differentSigns, new Expression.Construct(SpecBool(arithmetic), []));
                        remainder = new Expression.Binary("+", remainder, new Expression.Select(condition, right, zero), arithmetic);
                    }
                    result = SpecCast(type, remainder); break;
                case Op.ShiftLeftLogical: case Op.ShiftRightLogical: case Op.ShiftRightArithmetic:
                    Arity(2); SpecIntegerShape(type, V(0).Type);
                    if ((type is ShaderType.Vector tv ? tv.Size : 1) != (V(1).Type is ShaderType.Vector sv ? sv.Size : 1)) throw Error("Specialization shift dimensions differ.");
                    _ = SpecIntegerType(V(1).Type, false);
                    var integer = SpecIntegerType(type, op == Op.ShiftRightArithmetic);
                    var shiftType = V(1).Type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.U32) : (ShaderType)ShaderType.U32;
                    int bits = SpecComponent(integer).Width * 8;
                    var amount = new Expression.Binary("&", SpecCast(shiftType, V(1)), SpecUnsigned(shiftType, (ulong)(bits - 1)), shiftType);
                    Expression shifted = SpecCast(integer, V(0));
                    if (op == Op.ShiftLeftLogical)
                    {
                        ulong max = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
                        shifted = new Expression.Binary("&", shifted, new Expression.Binary(">>", SpecUnsigned(integer, max), amount, integer), integer);
                    }
                    result = SpecCast(type, new Expression.Binary(op == Op.ShiftLeftLogical ? "<<" : ">>", shifted, amount, integer)); break;
                case Op.BitwiseOr: case Op.BitwiseAnd: case Op.BitwiseXor:
                    Arity(2); SpecIntegerShape(type, V(0).Type); SpecIntegerShape(type, V(1).Type);
                    result = new Expression.Binary(BinaryOperator(op)!, SpecCast(type, V(0)), SpecCast(type, V(1)), type); break;
                case Op.LogicalAnd: case Op.LogicalOr:
                    Arity(2); SpecSame(type, SpecBool(type)); SpecSame(type, V(0).Type); SpecSame(type, V(1).Type);
                    var splat = new Expression.Construct(type, [Expression.Bool(op == Op.LogicalOr)]);
                    result = op == Op.LogicalAnd ? new Expression.Select(V(0), V(1), splat) : new Expression.Select(V(0), splat, V(1)); break;
                case Op.IEqual: case Op.INotEqual: case Op.LogicalEqual: case Op.LogicalNotEqual:
                case Op.SLessThan: case Op.SLessThanEqual: case Op.SGreaterThan: case Op.SGreaterThanEqual:
                case Op.ULessThan: case Op.ULessThanEqual: case Op.UGreaterThan: case Op.UGreaterThanEqual:
                    Arity(2); SpecSame(type, SpecBool(V(0).Type));
                    Expression x = V(0), y = V(1);
                    if (op is Op.LogicalEqual or Op.LogicalNotEqual)
                    { SpecSame(x.Type, SpecBool(x.Type)); SpecSame(x.Type, y.Type); }
                    else
                    {
                        SpecIntegerShape(x.Type, y.Type);
                        var compareType = SpecIntegerType(x.Type, op is Op.SLessThan or Op.SLessThanEqual or Op.SGreaterThan or Op.SGreaterThanEqual);
                        x = SpecCast(compareType, x); y = SpecCast(compareType, y);
                    }
                    result = new Expression.Binary(BinaryOperator(op)!, x, y, type); break;
                case Op.Select:
                    Arity(3); SpecSame(type, V(1).Type); SpecSame(type, V(2).Type);
                    result = SpecSelect(V(0), V(1), V(2)); break;
                case Op.CompositeExtract:
                    Count(5); result = V(0);
                    foreach (uint index in a[4..]) result = SpecIndex(result, index);
                    break;
                case Op.CompositeInsert:
                    Count(6); SpecSame(type, V(1).Type); result = SpecInsert(V(1), V(0), a.AsSpan(5)); break;
                case Op.VectorShuffle:
                    Count(6);
                    if (V(0).Type is not ShaderType.Vector first || V(1).Type is not ShaderType.Vector second || first.Component != second.Component
                        || type is not ShaderType.Vector destination || destination.Component != first.Component || a.Length - 5 != destination.Size)
                        throw Error("Invalid specialization vector shuffle.");
                    var parts = new List<Expression>();
                    foreach (uint index in a[5..])
                    {
                        if (index == uint.MaxValue) parts.Add(new Expression.Construct(first.Component, []));
                        else if (index < first.Size) parts.Add(Index(V(0), Expression.U32(index)));
                        else if (index - first.Size < second.Size) parts.Add(Index(V(1), Expression.U32(index - (uint)first.Size)));
                        else throw Error("Specialization shuffle index is out of bounds.");
                    }
                    result = new Expression.Construct(type, parts); break;
                default: throw Error($"Unsupported shader specialization operation {op}.");
            }
            SpecSame(type, result.Type); return result;
        }

        private void SpecSame(ShaderType expected, ShaderType actual)
        { if (expected != actual) throw Error("Specialization operation type mismatch."); }
        private void SpecIntegerShape(ShaderType expected, ShaderType actual) => SpecSame(SpecIntegerType(expected, false), SpecIntegerType(actual, false));
        private static Expression SpecCast(ShaderType type, Expression value) => type == value.Type ? value : new Expression.Convert(type, value);
        private ShaderType.Scalar SpecComponent(ShaderType type) => type switch
        { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, _ => throw Error("Specialization operation requires scalar/vector data.") };
        private ShaderType SpecIntegerType(ShaderType type, bool signed)
        {
            var scalar = SpecComponent(type);
            if (scalar.Kind is not (ScalarKind.Sint or ScalarKind.Uint)) throw Error("Specialization operation requires integers.");
            var target = new ShaderType.Scalar(signed ? ScalarKind.Sint : ScalarKind.Uint, scalar.Width);
            return type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, target) : target;
        }
        private static ShaderType SpecBool(ShaderType type) => type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.Bool) : ShaderType.Bool;
        private Expression SpecUnsigned(ShaderType type, ulong value, bool signed = false)
        {
            var scalar = SpecComponent(type);
            object payload = (signed, scalar.Width) switch
            {
                (true, 2) => (object)checked((short)value), (true, 4) => checked((int)value), (true, 8) => checked((long)value),
                (false, 2) => (object)checked((ushort)value), (false, 4) => checked((uint)value), _ => value
            };
            var literal = new Expression.Literal(payload, scalar);
            return type is ShaderType.Vector ? new Expression.Construct(type, [literal]) : literal;
        }

        private Expression SpecWrap(Op op, Expression left, Expression? right, ShaderType type)
        {
            // Half-width limbs keep every intermediate representable during
            // WGSL constant evaluation while retaining SPIR-V modular arithmetic.
            var unsigned = SpecIntegerType(type, false); int half = SpecComponent(unsigned).Width * 4;
            var mask = SpecUnsigned(unsigned, (1UL << half) - 1);
            var shift = unsigned is ShaderType.Vector v ? new Expression.Construct(new ShaderType.Vector(v.Size, ShaderType.U32), [Expression.U32((uint)half)]) : (Expression)Expression.U32((uint)half);
            Expression B(string operation, Expression a, Expression b) => new Expression.Binary(operation, a, b, unsigned);
            Expression Low(Expression a) => B("&", a, mask);
            Expression High(Expression a) => B(">>", a, shift);
            Expression Pack(Expression low, Expression high) => B("|", Low(low), B("<<", Low(high), shift));
            Expression Add(Expression a, Expression b)
            {
                var low = B("+", Low(a), Low(b));
                return Pack(low, B("+", B("+", High(a), High(b)), High(low)));
            }
            var x = SpecCast(unsigned, left); Expression result;
            if (op == Op.SNegate) result = Add(new Expression.Unary("~", x, unsigned), SpecUnsigned(unsigned, 1));
            else
            {
                var y = SpecCast(unsigned, right!);
                if (op == Op.IAdd) result = Add(x, y);
                else if (op == Op.ISub) result = Add(Add(x, new Expression.Unary("~", y, unsigned)), SpecUnsigned(unsigned, 1));
                else
                {
                    var product = B("*", Low(x), Low(y));
                    result = Pack(product, B("+", High(product), B("+", Low(B("*", High(x), Low(y))), Low(B("*", Low(x), High(y))))));
                }
            }
            return SpecCast(type, result);
        }

        private Expression SpecInsert(Expression aggregate, Expression value, ReadOnlySpan<uint> indices)
        {
            if (indices.IsEmpty) { SpecSame(aggregate.Type, value.Type); return value; }
            uint count = aggregate.Type switch
            {
                ShaderType.Vector v => (uint)v.Size, ShaderType.Matrix m => (uint)m.Columns,
                ShaderType.Structure s => (uint)s.Members.Count, ShaderType.Array { Length: uint n } => n,
                _ => throw Error("Specialization insertion requires a fixed composite type.")
            };
            uint selected = indices[0]; if (selected >= count) throw Error("Specialization insertion index is out of bounds.");
            var parts = new List<Expression>();
            for (uint i = 0; i < count; i++)
            {
                var part = SpecIndex(aggregate, i);
                parts.Add(i == selected ? SpecInsert(part, value, indices[1..]) : part);
            }
            return new Expression.Construct(aggregate.Type, parts);
        }

        private Expression SpecIndex(Expression aggregate, uint index)
        {
            uint count = aggregate.Type switch
            {
                ShaderType.Vector v => (uint)v.Size, ShaderType.Matrix m => (uint)m.Columns,
                ShaderType.Structure s => (uint)s.Members.Count, ShaderType.Array { Length: uint n } => n,
                _ => throw Error("Specialization extraction requires a fixed composite type.")
            };
            if (index >= count) throw Error("Specialization extraction index is out of bounds.");
            return Index(aggregate, Expression.U32(index));
        }

        private Expression SpecSelect(Expression condition, Expression accept, Expression reject)
        {
            if (accept.Type is ShaderType.Scalar or ShaderType.Vector)
            {
                if (condition.Type != ShaderType.Bool && condition.Type != SpecBool(accept.Type)) throw Error("Specialization select condition type mismatch.");
                return new Expression.Select(condition, accept, reject);
            }
            SpecSame(ShaderType.Bool, condition.Type);
            uint count = accept.Type switch
            {
                ShaderType.Matrix m => (uint)m.Columns, ShaderType.Structure s => (uint)s.Members.Count,
                ShaderType.Array { Length: uint n } => n, _ => throw Error("Unsupported specialization select type.")
            };
            var parts = new List<Expression>();
            for (uint i = 0; i < count; i++) parts.Add(SpecSelect(condition, SpecIndex(accept, i), SpecIndex(reject, i)));
            return new Expression.Construct(accept.Type, parts);
        }
    }
}

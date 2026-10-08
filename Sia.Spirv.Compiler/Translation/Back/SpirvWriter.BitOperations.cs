using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private sealed partial class FunctionEmitter
        {
            private uint CountOrReverseBits(string name, ShaderType type, uint value)
            {
                var scalar = Scalar(type); bool reverse = name == "reverseBits";
                var unsigned = new ShaderType.Scalar(ScalarKind.Uint, scalar.Width);
                ShaderType shape = type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, unsigned) : unsigned;
                ShaderType word = type is ShaderType.Vector w ? new ShaderType.Vector(w.Size, ShaderType.U32) : ShaderType.U32;
                uint bits = scalar.Kind == ScalarKind.Sint ? Result(Op.Bitcast, shape, value) : value;
                uint low = Result(Op.UConvert, word, bits), result;
                Op operation = reverse ? Op.BitReverse : Op.BitCount;
                if (scalar.Width == 2)
                {
                    result = Result(operation, word, low);
                    if (reverse) result = Result(Op.ShiftRightLogical, word, result, LiteralSplat(16u, word));
                    result = Result(Op.UConvert, shape, result);
                }
                else
                {
                    uint high = Result(Op.UConvert, word, Result(Op.ShiftRightLogical, shape, bits, LiteralSplat(32u, word)));
                    uint lower = Result(operation, word, low), upper = Result(operation, word, high);
                    result = reverse
                        ? Result(Op.BitwiseOr, shape, Result(Op.UConvert, shape, upper), Result(Op.ShiftLeftLogical, shape, Result(Op.UConvert, shape, lower), LiteralSplat(32u, word)))
                        : Result(Op.UConvert, shape, Result(Op.IAdd, word, lower, upper));
                }
                return scalar.Kind == ScalarKind.Sint ? Result(Op.Bitcast, type, result) : result;
            }

            private uint BitField(string name, ShaderType type, uint[] args)
            {
                var scalar = Scalar(type); uint width = (uint)scalar.Width * 8;
                bool insert = name == "insertBits"; int first = insert ? 2 : 1;
                uint offset = Glsl(38, ShaderType.U32, args[first], owner.Constant(Expression.U32(width)));
                uint count = Glsl(38, ShaderType.U32, args[first + 1], Result(Op.ISub, ShaderType.U32, owner.Constant(Expression.U32(width)), offset));
                if (scalar.Width is 2 or 4)
                {
                    ShaderType working = scalar.Width == 4 ? type : type is ShaderType.Vector v
                        ? new ShaderType.Vector(v.Size, scalar.Kind == ScalarKind.Sint ? ShaderType.I32 : ShaderType.U32)
                        : scalar.Kind == ScalarKind.Sint ? ShaderType.I32 : ShaderType.U32;
                    uint Widen(uint value) => scalar.Width == 4 ? value : Result(scalar.Kind == ScalarKind.Sint ? Op.SConvert : Op.UConvert, working, value);
                    uint result = insert ? Result(Op.BitFieldInsert, working, Widen(args[0]), Widen(args[1]), offset, count)
                        : Result(scalar.Kind == ScalarKind.Sint ? Op.BitFieldSExtract : Op.BitFieldUExtract, working, Widen(args[0]), offset, count);
                    return scalar.Width == 4 ? result : Result(scalar.Kind == ScalarKind.Sint ? Op.SConvert : Op.UConvert, type, result);
                }
                var unsigned = new ShaderType.Scalar(ScalarKind.Uint, 8);
                ShaderType shape = type is ShaderType.Vector u ? new ShaderType.Vector(u.Size, unsigned) : unsigned;
                ShaderType shiftType = type is ShaderType.Vector s ? new ShaderType.Vector(s.Size, ShaderType.U32) : ShaderType.U32;
                uint Shape(uint value) => shiftType is ShaderType.Vector vector ? Splat(value, vector) : value;
                uint Bits(uint value) => scalar.Kind == ScalarKind.Sint ? Result(Op.Bitcast, shape, value) : value;
                uint safeCount = Glsl(38, ShaderType.U32, count, owner.Constant(Expression.U32(63)));
                uint maskScalar = Result(Op.ISub, unsigned, Result(Op.ShiftLeftLogical, unsigned, owner.Constant(new Expression.Literal(1ul, unsigned)), safeCount), owner.Constant(new Expression.Literal(1ul, unsigned)));
                uint full = Result(Op.IEqual, ShaderType.Bool, count, owner.Constant(Expression.U32(64)));
                maskScalar = Result(Op.Select, unsigned, full, owner.Constant(new Expression.Literal(ulong.MaxValue, unsigned)), maskScalar);
                uint mask = shape is ShaderType.Vector m ? Splat(maskScalar, m) : maskScalar;
                uint safeOffset = Shape(Glsl(38, ShaderType.U32, offset, owner.Constant(Expression.U32(63))));
                uint resultBits;
                if (insert)
                {
                    mask = Result(Op.ShiftLeftLogical, shape, mask, safeOffset);
                    uint kept = Result(Op.BitwiseAnd, shape, Bits(args[0]), Result(Op.Not, shape, mask));
                    uint added = Result(Op.BitwiseAnd, shape, Result(Op.ShiftLeftLogical, shape, Bits(args[1]), safeOffset), mask);
                    resultBits = Result(Op.BitwiseOr, shape, kept, added);
                }
                else
                {
                    resultBits = Result(Op.BitwiseAnd, shape, Result(Op.ShiftRightLogical, shape, Bits(args[0]), safeOffset), mask);
                    if (scalar.Kind == ScalarKind.Sint)
                    {
                        uint signShift = Shape(Glsl(38, ShaderType.U32, Result(Op.ISub, ShaderType.U32, owner.Constant(Expression.U32(64)), count), owner.Constant(Expression.U32(63))));
                        uint extended = Result(Op.Bitcast, type, Result(Op.ShiftLeftLogical, shape, resultBits, signShift));
                        return Result(Op.ShiftRightArithmetic, type, extended, signShift);
                    }
                }
                return scalar.Kind == ScalarKind.Sint ? Result(Op.Bitcast, type, resultBits) : resultBits;
            }

            // GLSL Find{S,U}Msb and FindILsb require 32-bit components. Use a
            // logarithmic binary search for other widths, including vectors.
            private uint BitPosition(string name, ShaderType type, uint value)
            {
                var scalar = Scalar(type); int bits = scalar.Width * 8;
                var unsigned = new ShaderType.Scalar(ScalarKind.Uint, scalar.Width);
                ShaderType shape = type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, unsigned) : unsigned;
                ShaderType condition = type is ShaderType.Vector b ? new ShaderType.Vector(b.Size, ShaderType.Bool) : ShaderType.Bool;
                ShaderType shiftType = type is ShaderType.Vector s ? new ShaderType.Vector(s.Size, ShaderType.U32) : ShaderType.U32;
                uint Literal(ulong number) => LiteralSplat(number, shape);
                uint zero = owner.Null(shape);
                uint data = scalar.Kind == ScalarKind.Sint ? Result(Op.Bitcast, shape, value) : value;
                if (name == "firstLeadingBit" && scalar.Kind == ScalarKind.Sint)
                {
                    uint negative = Result(Op.SLessThan, condition, value, owner.Null(type));
                    data = Result(Op.Select, shape, negative, Result(Op.Not, shape, data), data);
                }
                uint empty = Result(Op.IEqual, condition, data, zero), position = zero;
                bool trailing = name is "firstTrailingBit" or "countTrailingZeros";
                for (int step = bits / 2; step != 0; step /= 2)
                {
                    uint shifted = Result(Op.ShiftRightLogical, shape, data, LiteralSplat((uint)step, shiftType));
                    uint test = trailing
                        ? Result(Op.IEqual, condition, Result(Op.BitwiseAnd, shape, data, Literal((1ul << step) - 1)), zero)
                        : Result(Op.INotEqual, condition, shifted, zero);
                    position = Result(Op.Select, shape, test, Result(Op.IAdd, shape, position, Literal((uint)step)), position);
                    data = Result(Op.Select, shape, test, shifted, data);
                }
                if (name == "countLeadingZeros") position = Result(Op.ISub, shape, Literal((uint)(bits - 1)), position);
                ulong absent = name.StartsWith("count", StringComparison.Ordinal) ? (uint)bits : bits == 64 ? ulong.MaxValue : (1ul << bits) - 1;
                position = Result(Op.Select, shape, empty, Literal(absent), position);
                return scalar.Kind == ScalarKind.Sint ? Result(Op.Bitcast, type, position) : position;
            }
        }
    }
}

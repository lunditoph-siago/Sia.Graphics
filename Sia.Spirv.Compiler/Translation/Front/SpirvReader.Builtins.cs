using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private Expression NativeGlslBuiltin(uint opcode, ShaderType resultType, Expression[] arguments)
        {
            string name = GlslFunction(opcode);
            void Arity(int count) { if (arguments.Length != count) throw Error("Invalid native builtin operand count."); }
            // SPIR-V encodes signedness in these opcodes independently of its
            // operand/result integer types. WGSL uses matching typed overloads.
            if (opcode is 5 or 7 or 38 or 39 or 41 or 42 or 44 or 45 or 73 or 74 or 75) {
                Arity(opcode is 44 or 45 ? 3 : opcode is 38 or 39 or 41 or 42 ? 2 : 1);
                bool? signed = opcode switch { 5 or 7 or 39 or 42 or 45 or 74 => true, 38 or 41 or 44 or 75 => false, _ => null };
                var type = signed is bool kind ? SpecIntegerType(arguments[0].Type, kind) : arguments[0].Type;
                return SpecCast(resultType, new Expression.Call(name, arguments.Select(a => SpecCast(type, a)).ToArray(), type, CallBinding.Builtin));
            }
            if (opcode == 53) {
                Arity(2);
                ShaderType exponent = arguments[0].Type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.I32) : ShaderType.I32;
                return new Expression.Call(name, [arguments[0], SpecCast(exponent, arguments[1])], resultType, CallBinding.Builtin);
            }
            if (resultType is ShaderType.Scalar scalar && opcode is 70 or 71 or 72) {
                Arity(opcode == 71 ? 2 : 3);
                Expression Binary(string op, Expression left, Expression right) => new Expression.Binary(op, left, right, scalar);
                if (opcode == 70) {
                    var product = Binary("*", arguments[1], arguments[2]);
                    var condition = new Expression.Binary("<", product, new Expression.Construct(scalar, []), ShaderType.Bool);
                    return new Expression.Select(condition, arguments[0], new Expression.Unary("-", arguments[0], scalar));
                }
                if (opcode == 71) {
                    var two = new Expression.Literal(NumericConversions.FloatValue(2, scalar), scalar);
                    return Binary("-", arguments[0], Binary("*", Binary("*", two, Binary("*", arguments[1], arguments[0])), arguments[1]));
                }
                var vector = new ShaderType.Vector(2, scalar);
                Expression Pad(Expression value) => new Expression.Construct(vector, [value, new Expression.Construct(scalar, [])]);
                var refracted = new Expression.Call(name, [Pad(arguments[0]), Pad(arguments[1]), arguments[2]], vector, CallBinding.Builtin);
                return new Expression.Swizzle(refracted, "x", scalar);
            }
            return new Expression.Call(name, arguments, resultType, CallBinding.Builtin);
        }
    }
}

using Sia.Spirv.Compiler.Compilation;
using System.Numerics;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using static Sia.Spirv.Compiler.Translation.Tests.SpirvTests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class RuntimeIntegerConversionTests
{
    private static BigInteger Wrap(BigInteger value, int bits, bool signed)
    {
        value &= (BigInteger.One << bits) - 1;
        return signed && value >= (BigInteger.One << (bits - 1)) ? value - (BigInteger.One << bits) : value;
    }

    private static Expression Input(ShaderType type, BigInteger raw)
    {
        var scalar = type is ShaderType.Vector v ? v.Component : (ShaderType.Scalar)type;
        BigInteger n = Wrap(raw, scalar.Width * 8, scalar.Kind == ScalarKind.Sint);
        object value = (scalar.Width, scalar.Kind) switch
        {
            (2, ScalarKind.Sint) => (object)(short)n, (2, _) => (ushort)n,
            (4, ScalarKind.Sint) => (int)n, (4, _) => (uint)n,
            (8, ScalarKind.Sint) => (long)n, _ => (ulong)n
        };
        var literal = new Expression.Literal(value, scalar);
        return type is ShaderType.Vector vector ? new Expression.Construct(type, Enumerable.Repeat<Expression>(literal, vector.Size).ToArray()) : literal;
    }

    private static BigInteger Evaluate(Module module, BigInteger input)
    {
        var function = Assert.Single(module.Functions);
        var argument = Assert.Single(function.Arguments);
        var values = new Dictionary<string, Expression> { [argument.Name] = Input(argument.Type, input) };
        Expression Bind(Expression e) => e switch
        {
            Expression.Reference r => values[r.Name],
            Expression.Load l => Bind(l.Pointer),
            Expression.Convert c => c with { Operand = Bind(c.Operand) },
            Expression.Binary b => b with { Left = Bind(b.Left), Right = Bind(b.Right) },
            Expression.Unary u => u with { Operand = Bind(u.Operand) },
            Expression.Construct c => c with { Components = c.Components.Select(Bind).ToArray() },
            _ => e
        };
        foreach (var statement in function.Body.Statements)
        {
            if (statement is Statement.Declare { Initializer: { } initializer } d) values[d.Name] = ConstantEvaluator.Evaluate(Bind(initializer));
            if (statement is Statement.Store { Target: Expression.Reference target } store) values[target.Name] = ConstantEvaluator.Evaluate(Bind(store.Value));
            if (statement is not Statement.Return r) continue;
            Expression actual = ConstantEvaluator.Evaluate(Bind(r.Value!));
            if (actual is Expression.Construct c)
            {
                Assert.Equal(c.Components[0], c.Components[1]); actual = c.Components[0];
            }
            return Assert.IsType<Expression.Literal>(actual).Value switch
            { short n => n, ushort n => n, int n => n, uint n => n, long n => n, ulong n => n, _ => throw new Exception("Expected integer") };
        }
        throw new Exception("Missing return");
    }

    [Theory]
    [InlineData((ushort)Op.ShiftRightArithmetic, 2147483648u, 1u, 3221225472u)]
    [InlineData((ushort)Op.SDiv, 4294967288u, 2u, 4294967292u)]
    [InlineData((ushort)Op.SRem, 4294967289u, 3u, 4294967295u)]
    public void SignedOpcodesInterpretUnsignedDeclaredBits(ushort opcode, uint input, uint operand, uint expected)
    {
        var code = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1),
            I(Op.TypeInt, 1, 32, 0), I(Op.TypeFunction, 2, 1, 1), I(Op.Constant, 1, 3, operand),
            I(Op.Function, 1, 10, 0, 2), I(Op.FunctionParameter, 1, 11), I(Op.Label, 12),
            I((Op)opcode, 1, 13, 11, 3), I(Op.ReturnValue, 13), I(Op.FunctionEnd));
        var module = SpirvReader.Parse(code.ToBytes());
        ModuleValidator.Validate(module);
        Assert.Equal(new BigInteger(expected), Evaluate(module, input));
        var roundtrip = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default));
        ModuleValidator.Validate(roundtrip);
        Assert.Equal(new BigInteger(expected), Evaluate(roundtrip, input));
    }

    [Theory]
    [InlineData(16, 32)] [InlineData(16, 64)] [InlineData(32, 16)]
    [InlineData(32, 64)] [InlineData(64, 16)] [InlineData(64, 32)]
    public void NativeConversionsInterpretBitsAccordingToOpcode(int from, int to)
    {
        foreach (bool sourceSigned in new[] { false, true })
        foreach (bool resultSigned in new[] { false, true })
        foreach (bool vector in new[] { false, true })
        foreach (var op in new[] { Op.UConvert, Op.SConvert })
        {
            if (resultSigned && op == Op.UConvert) continue;
            uint inputType = vector ? 3u : 1u, resultType = vector ? 4u : 2u;
            var code = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1),
                I(Op.TypeInt, 1, (uint)from, sourceSigned ? 1u : 0u), I(Op.TypeInt, 2, (uint)to, resultSigned ? 1u : 0u),
                I(Op.TypeVector, 3, 1, 2), I(Op.TypeVector, 4, 2, 2), I(Op.TypeFunction, 5, resultType, inputType),
                I(Op.Function, resultType, 10, 0, 5), I(Op.FunctionParameter, inputType, 11), I(Op.Label, 12),
                I(op, resultType, 13, 11), I(Op.ReturnValue, 13), I(Op.FunctionEnd));
            var module = SpirvReader.Parse(code.ToBytes()); ModuleValidator.Validate(module);
            var back = SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)); ModuleValidator.Validate(back);
            var wgsl = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)); ModuleValidator.Validate(wgsl);
            foreach (BigInteger raw in new[] { (BigInteger.One << from) - 1, (BigInteger.One << (from - 1)) + 3 })
            {
                BigInteger expected = Wrap(Wrap(raw, from, op == Op.SConvert), to, resultSigned);
                Assert.Equal(expected, Evaluate(module, raw)); Assert.Equal(expected, Evaluate(back, raw)); Assert.Equal(expected, Evaluate(wgsl, raw));
            }
        }
    }

    [Theory]
    [InlineData(16, 32)] [InlineData(16, 64)] [InlineData(32, 16)]
    [InlineData(32, 64)] [InlineData(64, 16)] [InlineData(64, 32)]
    public void NumericCastsExtendAccordingToSourceType(int from, int to)
    {
        foreach (bool sourceSigned in new[] { false, true })
        foreach (bool vector in new[] { false, true })
        {
            ShaderType Shape(int width, bool signed)
            {
                var scalar = new ShaderType.Scalar(signed ? ScalarKind.Sint : ScalarKind.Uint, width / 8);
                return vector ? new ShaderType.Vector(2, scalar) : scalar;
            }
            var source = Shape(from, sourceSigned); var destination = Shape(to, !sourceSigned);
            var module = new Module(); var function = new ShaderFunction("convert") { ReturnType = destination };
            function.Arguments.Add(new("input", source));
            function.Body.Statements.Add(new Statement.Return(new Expression.Convert(destination, new Expression.Reference("input", source))));
            module.Functions.Add(function); ModuleValidator.Validate(module);
            var back = SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)); ModuleValidator.Validate(back);
            foreach (BigInteger raw in new[] { (BigInteger.One << from) - 1, (BigInteger.One << (from - 1)) + 3 })
                Assert.Equal(Wrap(Wrap(raw, from, sourceSigned), to, !sourceSigned), Evaluate(back, raw));
        }
    }
}

using System.Numerics;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using static Sia.Spirv.Compiler.Translation.Tests.SpirvTests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpecializationOperationTests
{
    private static uint[] Words(BigInteger value, int bits)
    {
        value &= (BigInteger.One << bits) - 1;
        return bits == 64 ? [(uint)(value & uint.MaxValue), (uint)(value >> 32)] : [(uint)value];
    }

    private static Module Arithmetic(Op op, int bits, bool signed, bool vector, BigInteger x, BigInteger y, bool specialized = false)
    {
        var code = new List<SpirvInstruction> { I(Op.Capability, 1), I(Op.MemoryModel, 0, 1) };
        if (specialized) code.Add(I(Op.Decorate, 10, 1, 7));
        code.Add(I(Op.TypeInt, 1, (uint)bits, signed ? 1u : 0u));
        if (vector) code.Add(I(Op.TypeVector, 2, 1, 2));
        uint result = vector ? 2u : 1u;
        code.Add(I(Op.TypePointer, 4, 6, result));
        code.Add(I(Op.SpecConstant, new uint[] { 1, 10 }.Concat(Words(x, bits)).ToArray()));
        code.Add(I(Op.SpecConstant, new uint[] { 1, 11 }.Concat(Words(y, bits)).ToArray()));
        if (vector)
        {
            code.Add(I(Op.SpecConstantComposite, 2, 12, 10, 11));
            code.Add(I(Op.SpecConstantComposite, 2, 13, 11, 10));
        }
        uint left = vector ? 12u : 10u, right = vector ? 13u : 11u;
        code.Add(op == Op.SNegate ? I(Op.SpecConstantOp, result, 20, (uint)op, left) : I(Op.SpecConstantOp, result, 20, (uint)op, left, right));
        code.Add(I(Op.Variable, 4, 30, 6, 20));
        return SpirvReader.Parse(Binary(code.ToArray()).ToBytes());
    }

    private static BigInteger Number(Expression e) => Assert.IsType<Expression.Literal>(e).Value switch
    { short n => n, ushort n => n, int n => n, uint n => n, long n => n, ulong n => n, _ => throw new Exception("Expected integer") };
    private static BigInteger Wrap(BigInteger value, int bits, bool signed)
    {
        value &= (BigInteger.One << bits) - 1;
        return signed && value >= (BigInteger.One << (bits - 1)) ? value - (BigInteger.One << bits) : value;
    }

    [Theory]
    [InlineData(16, false, false)] [InlineData(16, true, false)]
    [InlineData(32, false, false)] [InlineData(32, true, false)]
    [InlineData(64, false, false)] [InlineData(64, true, false)]
    [InlineData(16, false, true)] [InlineData(16, true, true)]
    [InlineData(32, false, true)] [InlineData(32, true, true)]
    [InlineData(64, false, true)] [InlineData(64, true, true)]
    public void ModularArithmeticSurvivesStrictConstantEvaluation(int bits, bool signed, bool vector)
    {
        BigInteger max = (BigInteger.One << bits) - 1;
        BigInteger[] values = [0, 1, 2, max, max - 1, BigInteger.One << (bits - 1), (BigInteger.One << (bits / 2)) - 1, 0x12345678abcdef];
        foreach (var op in new[] { Op.IAdd, Op.ISub, Op.IMul, Op.SNegate })
        foreach (var rawX in values)
        foreach (var rawY in values)
        {
            BigInteger x = Wrap(rawX, bits, signed), y = Wrap(rawY, bits, signed);
            var module = Arithmetic(op, bits, signed, vector, x, y); ModuleValidator.Validate(module);
            var actual = ConstantEvaluator.Evaluate(module.Globals[0].Initializer!);
            BigInteger Expected(BigInteger a, BigInteger b) => Wrap(op switch { Op.IAdd => a + b, Op.ISub => a - b, Op.IMul => a * b, _ => -a }, bits, signed);
            if (vector)
            {
                var components = Assert.IsType<Expression.Construct>(actual).Components;
                Assert.Equal(Expected(x, y), Number(components[0])); Assert.Equal(Expected(y, x), Number(components[1]));
            }
            else Assert.Equal(Expected(x, y), Number(actual));
        }
        var boundary = Arithmetic(Op.IMul, bits, signed, vector, max, max);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(boundary)));
        Assert.NotEmpty(SpirvWriter.Write(boundary));
    }

    [Theory]
    [InlineData(-7, 3, -1, 2)] [InlineData(7, -3, 1, -2)]
    [InlineData(-7, -3, -1, -1)] [InlineData(7, 3, 1, 1)]
    [InlineData(int.MinValue, int.MaxValue, -1, int.MaxValue - 1)]
    [InlineData(int.MaxValue, int.MinValue, int.MaxValue, -1)]
    public void SignedModuloUsesDivisorSignAndRemainderUsesDividendSign(int x, int y, int remainder, int modulo)
    {
        foreach (bool vector in new[] { false, true })
        foreach (var op in new[] { Op.SRem, Op.SMod })
        {
            var module = Arithmetic(op, 32, true, vector, x, y);
            var actual = ConstantEvaluator.Evaluate(module.Globals[0].Initializer!);
            if (vector) actual = Assert.IsType<Expression.Construct>(actual).Components[0];
            Assert.Equal(new BigInteger(op == Op.SRem ? remainder : modulo), Number(actual));
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        }
    }

    [Theory]
    [InlineData((int)Op.ShiftLeftLogical, 0x80000001u, 1, 2u)]
    [InlineData((int)Op.ShiftRightLogical, 0x80000001u, 1, 0x40000000u)]
    [InlineData((int)Op.ShiftRightArithmetic, 0x80000001u, 1, 0xc0000000u)]
    public void ShiftOpcodeControlsSignedness(int opcode, uint x, uint y, uint expected)
    {
        var module = Arithmetic((Op)opcode, 32, false, false, x, y);
        Assert.Equal(new BigInteger(expected), Number(ConstantEvaluator.Evaluate(module.Globals[0].Initializer!)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Fact]
    public void PipelineValuesReachDerivedVectorOperations()
    {
        var module = Arithmetic(Op.IMul, 32, false, true, uint.MaxValue, 3, true);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["7"] = uint.MaxValue - 1 });
        var actual = Assert.IsType<Expression.Construct>(resolved.Globals[0].Initializer).Components;
        Assert.All(actual, e => Assert.Equal(new BigInteger(0xfffffffau), Number(e)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        Assert.NotEmpty(SpirvWriter.Write(resolved));
    }

    [Fact]
    public void MinimumSigned64BitValueUsesARepresentableLiteral()
    {
        var module = Arithmetic(Op.IAdd, 64, true, false, long.MinValue, 0);
        string text = WgslWriter.Write(module);
        Assert.Contains("i64(9223372036854775808lu)", text);
        var reread = WgslReader.Parse(text); ModuleValidator.Validate(reread);
        Assert.Equal(new BigInteger(long.MinValue), Number(ConstantEvaluator.Evaluate(reread.Globals[0].Initializer!)));
    }

    [Fact]
    public void WideSpecializationConstantsNeedResolutionForWgsl()
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse("override value=1lu;"));
        Assert.Throws<ShaderException>(() => WgslReader.Parse("override value=1li;"));
        var module = Arithmetic(Op.IAdd, 64, false, false, ulong.MaxValue, 1, true);
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double>());
        Assert.Equal(BigInteger.Zero, Number(resolved.Globals[0].Initializer!));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(resolved)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DerivedArrayLengthFoldsOrRetainsItsDependency(bool specialized)
    {
        var code = new List<SpirvInstruction> { I(Op.Capability, 1), I(Op.MemoryModel, 0, 1) };
        if (specialized) code.Add(I(Op.Decorate, 10, 1, 7));
        code.AddRange([I(Op.TypeInt, 1, 32, 0), I(Op.SpecConstant, 1, 10, 3), I(Op.Constant, 1, 11, 1),
            I(Op.SpecConstantOp, 1, 20, (uint)Op.IAdd, 10, 11), I(Op.TypeArray, 2, 1, 20), I(Op.TypeArray, 3, 1, 20),
            I(Op.TypePointer, 4, 4, 2), I(Op.Variable, 4, 30, 4)]);
        var module = SpirvReader.Parse(Binary(code.ToArray()).ToBytes()); ModuleValidator.Validate(module);
        var array = Assert.IsType<ShaderType.Array>(module.Globals[0].Type);
        if (specialized)
        {
            Assert.Null(array.Length); Assert.NotNull(array.OverrideLength); Assert.Equal(2, module.Constants.Count);
            var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["7"] = 8 });
            Assert.Equal(9u, Assert.IsType<ShaderType.Array>(resolved.Globals[0].Type).Length);
            Assert.NotEmpty(SpirvWriter.Write(resolved));
        }
        else Assert.Equal(4u, array.Length);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Fact]
    public void CompositeInsertExtractShuffleAndCompositeSelectRetainValues()
    {
        var binary = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), I(Op.TypeInt, 1, 32, 0), I(Op.TypeBool, 2),
            I(Op.TypeVector, 3, 1, 2), I(Op.Constant, 1, 10, 7), I(Op.Constant, 1, 11, 9), I(Op.SpecConstantTrue, 2, 12),
            I(Op.TypeStruct, 4, 3, 1), I(Op.TypePointer, 5, 6, 4), I(Op.SpecConstantComposite, 3, 13, 10, 11),
            I(Op.SpecConstantComposite, 4, 14, 13, 10), I(Op.SpecConstantOp, 4, 15, (uint)Op.CompositeInsert, 11, 14, 0, 0),
            I(Op.SpecConstantOp, 1, 16, (uint)Op.CompositeExtract, 15, 0, 0),
            I(Op.SpecConstantOp, 3, 17, (uint)Op.VectorShuffle, 13, 13, 3, 0), I(Op.SpecConstantComposite, 4, 18, 17, 16),
            I(Op.SpecConstantOp, 4, 20, (uint)Op.Select, 12, 18, 14), I(Op.Variable, 5, 30, 6, 20));
        var module = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(module);
        var value = Assert.IsType<Expression.Construct>(ConstantEvaluator.Evaluate(module.Globals[0].Initializer!));
        Assert.Equal(new BigInteger(9), Number(value.Components[1]));
        var vector = Assert.IsType<Expression.Construct>(value.Components[0]);
        Assert.Equal(new BigInteger(9), Number(vector.Components[0])); Assert.Equal(new BigInteger(7), Number(vector.Components[1]));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void LogicalScalarAndVectorOperationsRetainBooleanSemantics(bool x, bool y)
    {
        foreach (bool vector in new[] { false, true })
        foreach (var op in new[] { Op.LogicalAnd, Op.LogicalOr, Op.LogicalEqual, Op.LogicalNotEqual, Op.LogicalNot, Op.Select })
        {
            uint type = vector ? 2u : 1u, left = vector ? 12u : 10u, right = vector ? 13u : 11u;
            var operands = op == Op.LogicalNot ? new[] { left } : op == Op.Select ? new[] { left, left, right } : new[] { left, right };
            var binary = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), I(Op.TypeBool, 1), I(Op.TypeVector, 2, 1, 2), I(Op.TypePointer, 4, 6, type),
                I(x ? Op.SpecConstantTrue : Op.SpecConstantFalse, 1, 10), I(y ? Op.SpecConstantTrue : Op.SpecConstantFalse, 1, 11),
                I(Op.SpecConstantComposite, 2, 12, 10, 11), I(Op.SpecConstantComposite, 2, 13, 11, 10),
                I(Op.SpecConstantOp, new[] { type, 20u, (uint)op }.Concat(operands).ToArray()), I(Op.Variable, 4, 30, 6, 20));
            var module = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(module);
            bool Expected(bool a, bool b) => op switch { Op.LogicalAnd => a && b, Op.LogicalOr or Op.Select => a || b, Op.LogicalEqual => a == b, Op.LogicalNotEqual => a != b, _ => !a };
            var value = ConstantEvaluator.Evaluate(module.Globals[0].Initializer!);
            if (vector)
            {
                var parts = Assert.IsType<Expression.Construct>(value).Components;
                Assert.Equal(Expected(x, y), Assert.IsType<Expression.Literal>(parts[0]).Value);
                Assert.Equal(Expected(y, x), Assert.IsType<Expression.Literal>(parts[1]).Value);
            }
            else Assert.Equal(Expected(x, y), Assert.IsType<Expression.Literal>(value).Value);
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
            Assert.NotEmpty(SpirvWriter.Write(module));
        }
    }

    [Theory]
    [InlineData(16, 32)] [InlineData(16, 64)] [InlineData(32, 16)] [InlineData(32, 64)] [InlineData(64, 16)] [InlineData(64, 32)]
    public void IntegerConversionsFollowOpcodeSignednessAndDestinationWidth(int from, int to)
    {
        foreach (bool inputSigned in new[] { false, true })
        foreach (var op in new[] { Op.SConvert, Op.UConvert })
        foreach (bool vector in new[] { false, true })
        {
            uint inputType = vector ? 3u : 1u, outputType = vector ? 4u : 2u, operand = vector ? 11u : 10u;
            var binary = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), I(Op.TypeInt, 1, (uint)from, inputSigned ? 1u : 0u), I(Op.TypeInt, 2, (uint)to, 0),
                I(Op.TypeVector, 3, 1, 2), I(Op.TypeVector, 4, 2, 2), I(Op.TypePointer, 5, 6, outputType),
                I(Op.SpecConstant, new uint[] { 1, 10 }.Concat(Words(-1, from)).ToArray()), I(Op.SpecConstantComposite, 3, 11, 10, 10),
                I(Op.SpecConstantOp, outputType, 20, (uint)op, operand), I(Op.Variable, 5, 30, 6, 20));
            var module = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(module);
            var value = ConstantEvaluator.Evaluate(module.Globals[0].Initializer!);
            if (vector) value = Assert.IsType<Expression.Construct>(value).Components[0];
            Assert.Equal(Wrap(op == Op.SConvert ? -1 : (BigInteger.One << from) - 1, to, false), Number(value));
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FloatingConversionAndHalfQuantizationRoundCorrectly(bool vector)
    {
        uint f32 = vector ? 3u : 1u, f16 = vector ? 4u : 2u, operand = vector ? 11u : 10u;
        var binary = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), I(Op.TypeFloat, 1, 32), I(Op.TypeFloat, 2, 16),
            I(Op.TypeVector, 3, 1, 2), I(Op.TypeVector, 4, 2, 2), I(Op.TypePointer, 5, 6, f32),
            I(Op.SpecConstant, 1, 10, BitConverter.SingleToUInt32Bits(2.003f)), I(Op.SpecConstantComposite, 3, 11, 10, 10),
            I(Op.SpecConstantOp, f16, 20, (uint)Op.FConvert, operand), I(Op.SpecConstantOp, f32, 21, (uint)Op.FConvert, 20),
            I(Op.SpecConstantOp, f32, 22, (uint)Op.QuantizeToF16, 21), I(Op.Variable, 5, 30, 6, 22));
        var module = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(module);
        var value = ConstantEvaluator.Evaluate(module.Globals[0].Initializer!);
        if (vector) value = Assert.IsType<Expression.Construct>(value).Components[0];
        Assert.Equal((float)(Half)2.003f, Assert.IsType<Expression.Literal>(value).Value);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData((int)Op.IAdd, new uint[] { 10 })]
    [InlineData((int)Op.IAdd, new uint[] { 10, 99 })]
    [InlineData((int)Op.CompositeExtract, new uint[] { 10, 2 })]
    [InlineData((int)Op.VectorShuffle, new uint[] { 12, 12, 0, 4 })]
    [InlineData((int)Op.Bitcast, new uint[] { 10 })]
    public void MalformedOrKernelOnlyOperationsFailWithShaderDiagnostics(int opcode, uint[] operands)
    {
        var binary = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), I(Op.TypeInt, 1, 32, 0), I(Op.TypeVector, 2, 1, 2),
            I(Op.Constant, 1, 10, 7), I(Op.ConstantComposite, 2, 12, 10, 10), I(Op.SpecConstantOp, new uint[] { 1, 20, (uint)opcode }.Concat(operands).ToArray()));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(binary.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
    }

    private static Module Chain(int count, bool vector)
    {
        uint type = vector ? 2u : 1u;
        var code = new List<SpirvInstruction> { I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), I(Op.Decorate, 10, 1, 7),
            I(Op.TypeInt, 1, 32, 0), I(Op.TypeVector, 2, 1, 2), I(Op.TypePointer, 3, 6, type),
            I(Op.SpecConstant, 1, 10, 3), I(Op.Constant, 1, 11, 1), I(Op.SpecConstantComposite, 2, 12, 10, 10), I(Op.ConstantComposite, 2, 13, 11, 11) };
        uint previous = vector ? 12u : 10u;
        for (uint i = 0; i < count; i++)
        {
            uint next = 20 + i;
            code.Add(I(Op.SpecConstantOp, type, next, (uint)Op.IAdd, previous, vector ? 13u : 11u)); previous = next;
        }
        code.Add(I(Op.Variable, 3, (uint)(20 + count), 6, previous));
        return SpirvReader.Parse(new SpirvBinary { Bound = (uint)(21 + count), Instructions = code }.ToBytes());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LongSpecializationChainsRemainCompactAndResolveExactly(bool vector)
    {
        const int count = 512;
        var module = Chain(count, vector); ModuleValidator.Validate(module);
        Assert.Single(module.Constants, c => c.IsOverride);
        Assert.Equal(count * (vector ? 2 : 1), module.Constants.Count(c => c.IsSpecialization));
        string text = WgslWriter.Write(module);
        Assert.True(text.Length < count * (vector ? 1200 : 500), $"Specialization dependency expansion: {text.Length} characters.");
        ModuleValidator.Validate(WgslReader.Parse(text));
        byte[] bytes = SpirvWriter.Write(module);
        var binary = SpirvBinary.Parse(bytes);
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 1);
        Assert.True(binary.Instructions.Count < count * (vector ? 100 : 30));
        var values = new Dictionary<string, double> { ["7"] = uint.MaxValue - 4 };
        var resolved = PipelineConstantResolver.Resolve(module, values);
        var actual = resolved.Globals[0].Initializer!;
        if (vector) actual = Assert.IsType<Expression.Construct>(actual).Components[0];
        Assert.Equal(new BigInteger(count - 5), Number(actual));
        Assert.All(resolved.Constants, c => { Assert.False(c.IsOverride); Assert.False(c.IsSpecialization); });
    }

    [Fact]
    public void DerivedValuesHaveNoIndependentPipelineIdentity()
    {
        var module = Chain(3, false);
        string name = module.Constants.First(c => c.IsSpecialization).Name;
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { [name] = 9 }));
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.SpecConstantOp);
        var reread = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(reread);
        var resolved = PipelineConstantResolver.Resolve(reread, new Dictionary<string, double> { ["7"] = 8 });
        Assert.Equal(new BigInteger(11), Number(resolved.Globals[0].Initializer!));
    }

    [Theory]
    [InlineData(true, null, true)] [InlineData(false, 7u, true)] [InlineData(false, null, false)]
    public void InvalidSpecializationMetadataIsRejected(bool canOverride, uint? id, bool initializer)
    {
        var module = new Module(); module.Constants.Add(new("derived", ShaderType.U32, initializer ? Expression.U32(3) : null, canOverride, id) { IsSpecialization = true });
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }

    [Fact]
    public void CyclesAndMismatchedSpecializationInitializersAreRejected()
    {
        var cycle = new Module();
        cycle.Constants.Add(new("a", ShaderType.U32, new Expression.Reference("b", ShaderType.U32)) { IsSpecialization = true });
        cycle.Constants.Add(new("b", ShaderType.U32, new Expression.Reference("a", ShaderType.U32)) { IsSpecialization = true });
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(cycle));
        var mismatch = new Module(); mismatch.Constants.Add(new("a", ShaderType.U32, Expression.I32(3)) { IsSpecialization = true });
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(mismatch));
    }
}

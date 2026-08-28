using Sia.Spirv.Compiler.IL;

namespace Sia.Spirv.Compiler.Tests;

public sealed class CilInstructionDecoderTests
{
    [Fact]
    public void DecodeUsesTypedOperands()
    {
        ReadOnlySpan<byte> il = [
            0x1f, 0x80,
            0x0e, 0xff,
            0xfe, 0x09, 0xff, 0xff,
            0x21, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x22, 0x00, 0x00, 0xc0, 0x3f,
            0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x40,
            0x2a
        ];

        var instructions = CilInstructionDecoder.Decode(il);

        Assert.Equal(-128, Assert.IsType<int>(instructions[0].Operand.Value));
        Assert.Equal(255, Assert.IsType<int>(instructions[1].Operand.Value));
        Assert.Equal(ushort.MaxValue, Assert.IsType<int>(instructions[2].Operand.Value));
        Assert.Equal(0x0102030405060708, Assert.IsType<long>(instructions[3].Operand.Value));
        Assert.Equal(1.5f, Assert.IsType<float>(instructions[4].Operand.Value));
        Assert.Equal(2.5, Assert.IsType<double>(instructions[5].Operand.Value));
        Assert.Same(
            CilNoOperand.Instance,
            Assert.IsType<CilNoOperand>(instructions[6].Operand.Value));
    }

    [Fact]
    public void DecodeResolvesSwitchTargetsFromTheNextInstruction()
    {
        ReadOnlySpan<byte> il = [
            0x45,
            0x02, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00,
            0x00,
            0x00,
            0x2a
        ];

        var instructions = CilInstructionDecoder.Decode(il);

        Assert.Equal([13, 15], Assert.IsType<int[]>(instructions[0].Operand.Value));
    }

    [Fact]
    public void TypedAccessorReportsTheInstructionOffsetForAnUnexpectedOperand()
    {
        CilOperand operand = 1.5f;

        var exception = Assert.Throws<InvalidDataException>(() => operand.GetInt32(0x2a));

        Assert.Equal("Expected Int32 operand at IL_002a.", exception.Message);
    }
}

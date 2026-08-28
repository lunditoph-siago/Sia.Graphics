using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;

namespace Sia.Spirv.Compiler.IL;

internal static class CilInstructionDecoder
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> s_OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static opCode => unchecked((ushort)opCode.Value));

    public static IReadOnlyList<CilInstruction> Decode(ReadOnlySpan<byte> il)
    {
        var instructions = new List<CilInstruction>();
        var offset = 0;

        while (offset < il.Length) {
            var instructionOffset = offset;
            var value = il[offset++];
            ushort opCodeValue = value;
            if (value == 0xfe) {
                EnsureAvailable(il, offset, 1, instructionOffset);
                opCodeValue = (ushort)(0xfe00 | il[offset++]);
            }

            if (!s_OpCodesByValue.TryGetValue(opCodeValue, out var opCode)) {
                throw new InvalidDataException(
                    $"Unknown CIL opcode 0x{opCodeValue:x4} at IL_{instructionOffset:x4}.");
            }

            var operand = ReadOperand(il, ref offset, instructionOffset, opCode);
            instructions.Add(new CilInstruction(
                instructionOffset,
                opCode,
                operand,
                offset - instructionOffset));
        }

        return instructions;
    }

    private static CilOperand ReadOperand(
        ReadOnlySpan<byte> il,
        ref int offset,
        int instructionOffset,
        OpCode opCode)
    {
        switch (opCode.OperandType) {
            case OperandType.InlineNone:
                return CilOperand.None;
            case OperandType.ShortInlineBrTarget:
                EnsureAvailable(il, offset, 1, instructionOffset);
                return offset + 1 + unchecked((sbyte)il[offset++]);
            case OperandType.InlineBrTarget: {
                    EnsureAvailable(il, offset, 4, instructionOffset);
                    var delta = BinaryPrimitives.ReadInt32LittleEndian(il[offset..]);
                    offset += 4;
                    return offset + delta;
                }
            case OperandType.ShortInlineI:
                EnsureAvailable(il, offset, 1, instructionOffset);
                return (int)unchecked((sbyte)il[offset++]);
            case OperandType.InlineI:
                return ReadInt32(il, ref offset, instructionOffset);
            case OperandType.InlineI8: {
                    EnsureAvailable(il, offset, 8, instructionOffset);
                    var value = BinaryPrimitives.ReadInt64LittleEndian(il[offset..]);
                    offset += 8;
                    return value;
                }
            case OperandType.ShortInlineR: {
                    var bits = ReadInt32(il, ref offset, instructionOffset);
                    return BitConverter.Int32BitsToSingle(bits);
                }
            case OperandType.InlineR: {
                    EnsureAvailable(il, offset, 8, instructionOffset);
                    var bits = BinaryPrimitives.ReadInt64LittleEndian(il[offset..]);
                    offset += 8;
                    return BitConverter.Int64BitsToDouble(bits);
                }
            case OperandType.ShortInlineVar:
                EnsureAvailable(il, offset, 1, instructionOffset);
                return (int)il[offset++];
            case OperandType.InlineVar: {
                    EnsureAvailable(il, offset, 2, instructionOffset);
                    var index = BinaryPrimitives.ReadUInt16LittleEndian(il[offset..]);
                    offset += 2;
                    return (int)index;
                }
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
                return ReadInt32(il, ref offset, instructionOffset);
            case OperandType.InlineSwitch: {
                    var count = ReadInt32(il, ref offset, instructionOffset);
                    if (count < 0) {
                        throw new InvalidDataException(
                            $"Negative switch target count at IL_{instructionOffset:x4}.");
                    }
                    EnsureAvailable(il, offset, checked(count * 4), instructionOffset);
                    var nextInstruction = checked(offset + (count * 4));
                    var targets = new int[count];
                    for (var i = 0; i < count; i++) {
                        var delta = BinaryPrimitives.ReadInt32LittleEndian(il[offset..]);
                        offset += 4;
                        targets[i] = checked(nextInstruction + delta);
                    }
                    return targets;
                }
            default:
                throw new InvalidDataException(
                    $"Unsupported operand type {opCode.OperandType} at IL_{instructionOffset:x4}.");
        }
    }

    private static int ReadInt32(
        ReadOnlySpan<byte> il,
        ref int offset,
        int instructionOffset)
    {
        EnsureAvailable(il, offset, 4, instructionOffset);
        var value = BinaryPrimitives.ReadInt32LittleEndian(il[offset..]);
        offset += 4;
        return value;
    }

    private static void EnsureAvailable(
        ReadOnlySpan<byte> il,
        int offset,
        int count,
        int instructionOffset)
    {
        if (count < 0 || offset > il.Length - count) {
            throw new InvalidDataException(
                $"Truncated operand at IL_{instructionOffset:x4}.");
        }
    }
}

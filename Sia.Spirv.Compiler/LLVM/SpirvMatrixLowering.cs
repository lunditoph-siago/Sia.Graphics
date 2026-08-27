using System.Buffers.Binary;
using System.Text;

namespace Sia.Spirv.Compiler.LLVM;

internal static class SpirvMatrixLowering
{
    private const uint SpirvMagic = 0x07230203;
    private const ushort OpName = 5;
    private const ushort OpCapability = 17;
    private const ushort OpTypeFloat = 22;
    private const ushort OpTypeVector = 23;
    private const ushort OpTypeMatrix = 24;
    private const ushort OpTypeStruct = 30;
    private const uint MatrixCapability = 0;
    private const string MatrixTypePrefix = "sia.matrix.float";

    public static void Rewrite(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || bytes.Length % sizeof(uint) != 0) {
            throw new InvalidDataException("The SPIR-V module has an invalid byte length.");
        }

        var words = new uint[bytes.Length / sizeof(uint)];
        for (var index = 0; index < words.Length; index++) {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)));
        }
        if (words[0] != SpirvMagic) {
            throw new InvalidDataException("The SPIR-V module has an invalid magic number.");
        }

        var names = new Dictionary<uint, string>();
        var vectors = new Dictionary<uint, (uint ComponentType, uint Length)>();
        var floatWidths = new Dictionary<uint, uint>();
        foreach (var instruction in EnumerateInstructions(words)) {
            switch (instruction.Opcode) {
                case OpName:
                    names[instruction.Words[1]] = DecodeString(instruction.Words[2..]);
                    break;
                case OpTypeFloat:
                    floatWidths[instruction.Words[1]] = instruction.Words[2];
                    break;
                case OpTypeVector:
                    vectors[instruction.Words[1]] = (instruction.Words[2], instruction.Words[3]);
                    break;
            }
        }

        var matrixTypes = new Dictionary<uint, MatrixShape>();
        foreach (var instruction in EnumerateInstructions(words)) {
            if (instruction.Opcode != OpTypeStruct ||
                !names.TryGetValue(instruction.Words[1], out var name) ||
                !TryParseMatrixShape(name, out var shape)) {
                continue;
            }

            var members = instruction.Words[2..];
            if (members.Length != shape.Columns ||
                members.Any(member => member != members[0]) ||
                !vectors.TryGetValue(members[0], out var vector) ||
                vector.Length != shape.Rows ||
                !floatWidths.TryGetValue(vector.ComponentType, out var width) ||
                width != 32) {
                throw new InvalidDataException($"SPIR-V matrix marker '{name}' has an invalid representation.");
            }
            matrixTypes.Add(instruction.Words[1], shape with { ColumnType = members[0] });
        }
        if (matrixTypes.Count == 0) {
            return;
        }

        var output = new List<uint>(words.Length + 2);
        output.AddRange(words.AsSpan(0, 5).ToArray());
        var insertedCapability = false;
        var hasMatrixCapability = false;
        foreach (var instruction in EnumerateInstructions(words)) {
            if (instruction.Opcode == OpCapability && instruction.Words[1] == MatrixCapability) {
                hasMatrixCapability = true;
            }
            if (!insertedCapability && instruction.Opcode != OpCapability) {
                if (!hasMatrixCapability) {
                    output.Add(MakeInstructionHeader(2, OpCapability));
                    output.Add(MatrixCapability);
                }
                insertedCapability = true;
            }

            if (instruction.Opcode == OpTypeStruct &&
                matrixTypes.TryGetValue(instruction.Words[1], out var shape)) {
                output.Add(MakeInstructionHeader(4, OpTypeMatrix));
                output.Add(instruction.Words[1]);
                output.Add(shape.ColumnType);
                output.Add(shape.Columns);
            }
            else {
                output.AddRange(instruction.Words.ToArray());
            }
        }

        var outputBytes = new byte[output.Count * sizeof(uint)];
        for (var index = 0; index < output.Count; index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(
                outputBytes.AsSpan(index * sizeof(uint)),
                output[index]);
        }
        File.WriteAllBytes(path, outputBytes);
    }

    private static IEnumerable<Instruction> EnumerateInstructions(uint[] words)
    {
        var offset = 5;
        while (offset < words.Length) {
            var header = words[offset];
            var wordCount = checked((int)(header >> 16));
            if (wordCount <= 0 || offset + wordCount > words.Length) {
                throw new InvalidDataException($"Invalid SPIR-V instruction at word {offset}.");
            }
            yield return new Instruction(
                (ushort)(header & ushort.MaxValue),
                words.AsSpan(offset, wordCount).ToArray());
            offset += wordCount;
        }
    }

    private static string DecodeString(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }
        var length = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }

    private static bool TryParseMatrixShape(string name, out MatrixShape shape)
    {
        shape = default;
        if (!name.StartsWith(MatrixTypePrefix, StringComparison.Ordinal)) {
            return false;
        }
        var suffix = name.AsSpan(MatrixTypePrefix.Length);
        if (suffix.Length != 3 || suffix[1] != 'x' ||
            suffix[0] is < '2' or > '4' || suffix[2] is < '2' or > '4') {
            return false;
        }
        shape = new MatrixShape((uint)(suffix[0] - '0'), (uint)(suffix[2] - '0'), 0);
        return true;
    }

    private static uint MakeInstructionHeader(uint wordCount, ushort opcode) =>
        wordCount << 16 | opcode;

    private readonly record struct Instruction(ushort Opcode, uint[] Words);

    private readonly record struct MatrixShape(uint Rows, uint Columns, uint ColumnType);
}

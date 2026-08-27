using System.Buffers.Binary;

namespace Sia.Spirv.Compiler.LLVM;

internal static class SpirvSignedConversionLowering
{
    private const uint SpirvMagic = 0x07230203;
    private const ushort OpTypeInt = 21;
    private const ushort OpTypeVector = 23;
    private const ushort OpFunction = 54;
    private const ushort OpConvertSToF = 111;
    private const ushort OpBitcast = 124;

    public static void Rewrite(string path)
    {
        var words = ReadWords(path);
        var instructions = EnumerateInstructions(words).ToArray();
        var integerTypes = new Dictionary<uint, IntegerType>();
        var vectorTypes = new Dictionary<uint, VectorType>();
        foreach (var instruction in instructions) {
            switch (instruction.Opcode) {
                case OpTypeInt:
                    integerTypes[instruction.Words[1]] = new IntegerType(
                        instruction.Words[2],
                        instruction.Words[3] != 0);
                    break;
                case OpTypeVector:
                    vectorTypes[instruction.Words[1]] = new VectorType(
                        instruction.Words[2],
                        instruction.Words[3]);
                    break;
            }
        }

        var valueTypes = GetValueTypes(instructions, integerTypes.Keys.Concat(vectorTypes.Keys));
        var nextId = words[3];
        var addedTypes = new List<uint[]>();
        var replacements = new Dictionary<int, ConversionReplacement>();
        for (var index = 0; index < instructions.Length; index++) {
            var instruction = instructions[index];
            if (instruction.Opcode != OpConvertSToF) {
                continue;
            }
            var operand = instruction.Words[3];
            if (!valueTypes.TryGetValue(operand, out var operandType)) {
                throw new InvalidDataException("Unable to determine the operand type of OpConvertSToF.");
            }
            var signedType = GetSignedType(
                operandType,
                integerTypes,
                vectorTypes,
                addedTypes,
                ref nextId);
            if (signedType == operandType) {
                continue;
            }
            replacements[index] = new ConversionReplacement(signedType, nextId++);
        }
        if (replacements.Count == 0) {
            return;
        }

        var output = new List<uint>(words.Length + addedTypes.Sum(static type => type.Length) + replacements.Count * 4);
        output.AddRange(words.AsSpan(0, 5).ToArray());
        output[3] = nextId;
        var insertedTypes = false;
        for (var index = 0; index < instructions.Length; index++) {
            var instruction = instructions[index];
            if (!insertedTypes && instruction.Opcode == OpFunction) {
                foreach (var type in addedTypes) {
                    output.AddRange(type);
                }
                insertedTypes = true;
            }
            if (!replacements.TryGetValue(index, out var replacement)) {
                output.AddRange(instruction.Words);
                continue;
            }

            output.Add(MakeInstructionHeader(4, OpBitcast));
            output.Add(replacement.SignedType);
            output.Add(replacement.ResultId);
            output.Add(instruction.Words[3]);
            var converted = (uint[])instruction.Words.Clone();
            converted[3] = replacement.ResultId;
            output.AddRange(converted);
        }

        WriteWords(path, output);
    }

    private static Dictionary<uint, uint> GetValueTypes(
        IReadOnlyList<Instruction> instructions,
        IEnumerable<uint> typeIds)
    {
        var types = typeIds.ToHashSet();
        var result = new Dictionary<uint, uint>();
        foreach (var instruction in instructions) {
            if (instruction.Opcode is >= 19 and <= 39 ||
                instruction.Words.Length < 3 ||
                !types.Contains(instruction.Words[1])) {
                continue;
            }
            result[instruction.Words[2]] = instruction.Words[1];
        }
        return result;
    }

    private static uint GetSignedType(
        uint typeId,
        IDictionary<uint, IntegerType> integerTypes,
        IDictionary<uint, VectorType> vectorTypes,
        ICollection<uint[]> addedTypes,
        ref uint nextId)
    {
        if (integerTypes.TryGetValue(typeId, out var integer)) {
            return integer.Signed
                ? typeId
                : GetSignedIntegerType(integer.Width, integerTypes, addedTypes, ref nextId);
        }
        if (!vectorTypes.TryGetValue(typeId, out var vector) ||
            !integerTypes.TryGetValue(vector.ComponentType, out integer)) {
            throw new InvalidDataException("OpConvertSToF has a non-integer operand type.");
        }
        if (integer.Signed) {
            return typeId;
        }

        var signedComponent = GetSignedIntegerType(integer.Width, integerTypes, addedTypes, ref nextId);
        foreach (var (candidateId, candidate) in vectorTypes) {
            if (candidate.ComponentType == signedComponent && candidate.Length == vector.Length) {
                return candidateId;
            }
        }
        var result = nextId++;
        vectorTypes[result] = new VectorType(signedComponent, vector.Length);
        addedTypes.Add([
            MakeInstructionHeader(4, OpTypeVector),
            result,
            signedComponent,
            vector.Length
        ]);
        return result;
    }

    private static uint GetSignedIntegerType(
        uint width,
        IDictionary<uint, IntegerType> integerTypes,
        ICollection<uint[]> addedTypes,
        ref uint nextId)
    {
        foreach (var (candidateId, candidate) in integerTypes) {
            if (candidate.Width == width && candidate.Signed) {
                return candidateId;
            }
        }
        var result = nextId++;
        integerTypes[result] = new IntegerType(width, true);
        addedTypes.Add([
            MakeInstructionHeader(4, OpTypeInt),
            result,
            width,
            1
        ]);
        return result;
    }

    private static uint[] ReadWords(string path)
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
        return words;
    }

    private static void WriteWords(string path, IReadOnlyList<uint> words)
    {
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }
        File.WriteAllBytes(path, bytes);
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

    private static uint MakeInstructionHeader(uint wordCount, ushort opcode) =>
        wordCount << 16 | opcode;

    private readonly record struct Instruction(ushort Opcode, uint[] Words);

    private readonly record struct IntegerType(uint Width, bool Signed);

    private readonly record struct VectorType(uint ComponentType, uint Length);

    private readonly record struct ConversionReplacement(uint SignedType, uint ResultId);
}

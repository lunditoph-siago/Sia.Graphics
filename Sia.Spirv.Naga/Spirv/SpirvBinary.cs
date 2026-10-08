using System.Buffers.Binary;
using System.Text;

namespace Sia.Spirv.Naga.Spirv;

public sealed record SpirvInstruction(ushort Opcode, uint[] Operands, int WordOffset = 0)
{
    public int WordCount => Operands.Length + 1;
}

/// <summary>Lossless SPIR-V framing. Semantic validation belongs to the frontend.</summary>
public sealed class SpirvBinary
{
    public const uint Magic = 0x07230203;
    public uint Version { get; init; } = 0x00010300;
    public uint Generator { get; init; }
    public uint Bound { get; init; }
    public IReadOnlyList<SpirvInstruction> Instructions { get; init; } = [];

    public static SpirvBinary Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || bytes.Length % 4 != 0)
            throw Error("SPIR-V must contain a five-word header and whole 32-bit words.", 0);
        bool littleEndian = BinaryPrimitives.ReadUInt32LittleEndian(bytes) switch
        {
            Magic => true,
            0x03022307 => false,
            _ => throw Error("Invalid SPIR-V magic number.", 0)
        };
        var words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++)
            words[i] = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..])
                : BinaryPrimitives.ReadUInt32BigEndian(bytes[(i * 4)..]);
        return Parse(words);
    }

    public static SpirvBinary Parse(ReadOnlySpan<uint> words)
    {
        if (words.Length < 5 || words[0] != Magic) throw Error("Invalid SPIR-V header.", 0);
        uint version = words[1];
        if ((version & 0xff0000ff) != 0 || (version >> 16) != 1 || ((version >> 8) & 255) > 6)
            throw Error($"Unsupported SPIR-V version 0x{version:x8}.", 1);
        if (words[3] == 0 || words[3] > 0x3fffff) throw Error("ID bound is outside the SPIR-V range.", 3);
        if (words[4] != 0) throw Error("Reserved schema word must be zero.", 4);
        var instructions = new List<SpirvInstruction>();
        for (int offset = 5; offset < words.Length;)
        {
            int count = (int)(words[offset] >> 16);
            if (count == 0) throw Error("Instruction word count cannot be zero.", offset);
            if (count > words.Length - offset) throw Error("Truncated SPIR-V instruction.", offset);
            instructions.Add(new((ushort)words[offset], words.Slice(offset + 1, count - 1).ToArray(), offset));
            offset += count;
        }
        return new() { Version = version, Generator = words[2], Bound = words[3], Instructions = instructions };
    }

    public uint[] ToWords()
    {
        int count = 5;
        foreach (var instruction in Instructions)
        {
            if (instruction.WordCount > ushort.MaxValue) throw Error("Instruction exceeds 65535 words.", count);
            count = checked(count + instruction.WordCount);
        }
        var words = new uint[count];
        words[0] = Magic; words[1] = Version; words[2] = Generator; words[3] = Bound;
        int offset = 5;
        foreach (var instruction in Instructions)
        {
            words[offset++] = ((uint)instruction.WordCount << 16) | instruction.Opcode;
            instruction.Operands.CopyTo(words, offset);
            offset += instruction.Operands.Length;
        }
        return words;
    }

    public byte[] ToBytes()
    {
        uint[] words = ToWords();
        var bytes = new byte[checked(words.Length * 4)];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    internal static string ReadString(ReadOnlySpan<uint> words, out int consumed)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < words.Length; i++)
        {
            uint word = words[i];
            for (int j = 0; j < 4; j++)
            {
                byte value = (byte)(word >> (j * 8));
                if (value == 0)
                {
                    if (j < 3 && (word >> ((j + 1) * 8)) != 0) throw Error("Nonzero string padding.", 0);
                    consumed = i + 1;
                    try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
                    catch (DecoderFallbackException) { throw Error("Invalid UTF-8 string.", 0); }
                }
                bytes.Add(value);
            }
        }
        throw Error("Unterminated SPIR-V string.", 0);
    }

    internal static uint[] StringWords(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var words = new uint[(bytes.Length + 4) / 4];
        for (int i = 0; i < bytes.Length; i++) words[i / 4] |= (uint)bytes[i] << ((i % 4) * 8);
        return words;
    }

    private static ShaderException Error(string message, int offset) => new(DiagnosticStage.SpirvParse, message, new(offset * 4, 4));
}

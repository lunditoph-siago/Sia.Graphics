using System.Buffers.Binary;

namespace Sia.Spirv.Compiler.LLVM;

internal static class SpirvWorkgroupInitializerLowering
{
    private const uint SpirvMagic = 0x07230203;
    private const ushort OpVariable = 59;
    private const uint WorkgroupStorageClass = 4;

    public static void Rewrite(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || bytes.Length % sizeof(uint) != 0) {
            throw new InvalidDataException("The SPIR-V module has an invalid byte length.");
        }
        var words = new uint[bytes.Length / sizeof(uint)];
        for (var index = 0; index < words.Length; index++) {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)));
        }
        if (words[0] != SpirvMagic) {
            throw new InvalidDataException("The SPIR-V module has an invalid magic number.");
        }

        var output = new List<uint>(words.Length);
        output.AddRange(words.AsSpan(0, 5).ToArray());
        var offset = 5;
        var changed = false;
        while (offset < words.Length) {
            var header = words[offset];
            var wordCount = checked((int)(header >> 16));
            var opcode = (ushort)(header & ushort.MaxValue);
            if (wordCount <= 0 || offset + wordCount > words.Length) {
                throw new InvalidDataException($"Invalid SPIR-V instruction at word {offset}.");
            }
            if (opcode == OpVariable && wordCount == 5 &&
                words[offset + 3] == WorkgroupStorageClass) {
                output.Add(4u << 16 | OpVariable);
                output.Add(words[offset + 1]);
                output.Add(words[offset + 2]);
                output.Add(words[offset + 3]);
                changed = true;
            } else {
                output.AddRange(words.AsSpan(offset, wordCount).ToArray());
            }
            offset += wordCount;
        }
        if (!changed) {
            return;
        }

        bytes = new byte[output.Count * sizeof(uint)];
        for (var index = 0; index < output.Count; index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                output[index]);
        }
        File.WriteAllBytes(path, bytes);
    }
}

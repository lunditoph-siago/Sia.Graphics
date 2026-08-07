namespace Sia.Graphics.Text;

public sealed partial class Font
{
    public float GetKerning(ushort leftGlyphId, ushort rightGlyphId) =>
        _kerning.GetValueOrDefault(((uint)leftGlyphId << 16) | rightGlyphId);

    private Dictionary<uint, short> ReadKerningPairs()
    {
        var result = new Dictionary<uint, short>();
        if (!TryGetTable("kern", out var table) || table.Length < 4)
            return result;

        var reader = new BigEndianReader(table);
        var version = reader.ReadUInt16();
        if (version != 0)
            return result;
        var tableCount = reader.ReadUInt16();
        for (var tableIndex = 0; tableIndex < tableCount && reader.Position + 6 <= table.Length; tableIndex++) {
            var subtableStart = reader.Position;
            reader.ReadUInt16();
            var length = reader.ReadUInt16();
            var coverage = reader.ReadUInt16();
            var format = coverage >> 8;
            var horizontal = (coverage & 0x0001) != 0;
            var subtableEnd = Math.Min(table.Length, subtableStart + length);
            if (format == 0 && horizontal && reader.Position + 8 <= subtableEnd) {
                var pairCount = reader.ReadUInt16();
                reader.Position += 6;
                for (var pairIndex = 0;
                    pairIndex < pairCount && reader.Position + 6 <= subtableEnd;
                    pairIndex++) {
                    var left = reader.ReadUInt16();
                    var right = reader.ReadUInt16();
                    var value = reader.ReadInt16();
                    result[((uint)left << 16) | right] = value;
                }
            }
            reader.Position = subtableEnd;
        }
        return result;
    }
}

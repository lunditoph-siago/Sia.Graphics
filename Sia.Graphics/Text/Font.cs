namespace Sia.Graphics.Text;

public sealed partial class Font
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (uint Offset, uint Length)> _tables;
    private readonly Dictionary<uint, short> _kerning;

    public ushort UnitsPerEm { get; }
    public short IndexToLocFormat { get; }
    public ushort NumGlyphs { get; }
    public ushort NumberOfHMetrics { get; }
    public short Ascender { get; }
    public short Descender { get; }

    public Font(byte[] data)
    {
        _data = data;
        _tables = ReadTableDirectory(data);

        var head = ReadHead();
        UnitsPerEm = head.UnitsPerEm;
        IndexToLocFormat = head.IndexToLocFormat;
        NumGlyphs = ReadMaxp();

        var hhea = ReadHhea();
        Ascender = hhea.Ascender;
        Descender = hhea.Descender;
        NumberOfHMetrics = hhea.NumberOfHMetrics;
        _kerning = ReadKerningPairs();
    }

    public static Font Load(string path) => new(File.ReadAllBytes(path));

    private bool TryGetTable(string tag, out ReadOnlySpan<byte> table)
    {
        if (_tables.TryGetValue(tag, out var entry)) {
            table = _data.AsSpan((int)entry.Offset, (int)entry.Length);
            return true;
        }
        table = default;
        return false;
    }

    private static Dictionary<string, (uint Offset, uint Length)> ReadTableDirectory(byte[] data)
    {
        if (data.Length < 12)
            throw new InvalidDataException("The font is shorter than the SFNT header.");
        var reader = new BigEndianReader(data);
        reader.ReadUInt32(); // sfntVersion
        var numTables = reader.ReadUInt16();
        reader.ReadUInt16(); // searchRange
        reader.ReadUInt16(); // entrySelector
        reader.ReadUInt16(); // rangeShift

        if (numTables > (data.Length - 12) / 16)
            throw new InvalidDataException("The SFNT table directory is truncated.");

        var tables = new Dictionary<string, (uint, uint)>(numTables);
        for (var i = 0; i < numTables; i++) {
            var tagBytes = reader.Slice(reader.Position, 4);
            var tag = System.Text.Encoding.ASCII.GetString(tagBytes);
            reader.Position += 4;
            reader.ReadUInt32(); // checksum
            var offset = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            if ((ulong)offset + length > (ulong)data.Length)
                throw new InvalidDataException($"Font table '{tag}' extends beyond the file.");
            tables[tag] = (offset, length);
        }
        return tables;
    }
}

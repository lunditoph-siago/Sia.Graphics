namespace Sia.Graphics.Text;

public sealed partial class Font
{
    private (ushort UnitsPerEm, short IndexToLocFormat) ReadHead()
    {
        if (!TryGetTable("head", out var table))
            throw new InvalidDataException("Font is missing the required 'head' table.");
        if (table.Length < 54)
            throw new InvalidDataException("Font table 'head' is truncated.");

        var reader = new BigEndianReader(table) { Position = 18 };
        var unitsPerEm = reader.ReadUInt16();
        reader.Position = 50;
        var indexToLocFormat = reader.ReadInt16();
        return (unitsPerEm, indexToLocFormat);
    }

    private ushort ReadMaxp()
    {
        if (!TryGetTable("maxp", out var table))
            throw new InvalidDataException("Font is missing the required 'maxp' table.");
        if (table.Length < 6)
            throw new InvalidDataException("Font table 'maxp' is truncated.");

        var reader = new BigEndianReader(table) { Position = 4 };
        return reader.ReadUInt16();
    }

    private (short Ascender, short Descender, ushort NumberOfHMetrics) ReadHhea()
    {
        if (!TryGetTable("hhea", out var table))
            throw new InvalidDataException("Font is missing the required 'hhea' table.");
        if (table.Length < 36)
            throw new InvalidDataException("Font table 'hhea' is truncated.");

        var reader = new BigEndianReader(table) { Position = 4 };
        var ascender = reader.ReadInt16();
        var descender = reader.ReadInt16();
        reader.Position = 34;
        var numberOfHMetrics = reader.ReadUInt16();
        return (ascender, descender, numberOfHMetrics);
    }

    public float GetAdvanceWidth(ushort glyphId)
    {
        if (!TryGetTable("hmtx", out var table) || NumberOfHMetrics == 0)
            return 0f;

        var index = System.Math.Min(glyphId, (ushort)(NumberOfHMetrics - 1));
        if ((index + 1) * 4 > table.Length)
            return 0f;
        var reader = new BigEndianReader(table) { Position = index * 4 };
        return reader.ReadUInt16();
    }
}

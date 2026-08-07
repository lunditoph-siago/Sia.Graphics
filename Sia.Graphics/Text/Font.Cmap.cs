namespace Sia.Graphics.Text;

public sealed partial class Font
{
    public ushort GetGlyphIndex(int codepoint)
    {
        if (!TryGetTable("cmap", out var cmap))
            return 0;

        var directory = new BigEndianReader(cmap);
        directory.ReadUInt16(); // version
        var numTables = directory.ReadUInt16();

        var bestOffset = -1;
        var bestFormat = -1;
        var bestRank = -1;
        for (var i = 0; i < numTables; i++) {
            var platformId = directory.ReadUInt16();
            var encodingId = directory.ReadUInt16();
            var offset = (int)directory.ReadUInt32();

            var format = new BigEndianReader(cmap) { Position = offset }.ReadUInt16();
            var rank = SubtableRank(platformId, encodingId, format);
            if (rank > bestRank) {
                bestRank = rank;
                bestOffset = offset;
                bestFormat = format;
            }
        }

        if (bestOffset < 0 || bestRank < 0)
            return 0;

        return bestFormat switch {
            4 => LookupFormat4(cmap, bestOffset, codepoint),
            12 => LookupFormat12(cmap, bestOffset, codepoint),
            0 => LookupFormat0(cmap, bestOffset, codepoint),
            _ => 0
        };
    }

    private static int SubtableRank(int platformId, int encodingId, int format) => format switch {
        12 => (platformId == 3 && encodingId == 10) || (platformId == 0 && encodingId >= 4) ? 3 : 2,
        4 => 1,
        0 => 0,
        _ => -1
    };

    private static ushort LookupFormat0(ReadOnlySpan<byte> cmap, int offset, int codepoint)
    {
        if (codepoint is < 0 or > 255)
            return 0;
        var reader = new BigEndianReader(cmap) { Position = offset + 6 + codepoint };
        return reader.ReadUInt8();
    }

    private static ushort LookupFormat4(ReadOnlySpan<byte> cmap, int offset, int codepoint)
    {
        if (codepoint > 0xFFFF)
            return 0;

        var header = new BigEndianReader(cmap) { Position = offset + 6 };
        var segCountX2 = header.ReadUInt16();
        var segCount = segCountX2 / 2;
        header.Position += 6; // searchRange, entrySelector, rangeShift

        var endCodesPos = offset + 14;
        var startCodesPos = endCodesPos + segCountX2 + 2; // + reservedPad
        var idDeltaPos = startCodesPos + segCountX2;
        var idRangeOffsetPos = idDeltaPos + segCountX2;

        for (var i = 0; i < segCount; i++) {
            var endCode = new BigEndianReader(cmap) { Position = endCodesPos + i * 2 }.ReadUInt16();
            if (codepoint > endCode)
                continue;

            var startCode = new BigEndianReader(cmap) { Position = startCodesPos + i * 2 }.ReadUInt16();
            if (codepoint < startCode)
                return 0;

            var idDelta = new BigEndianReader(cmap) { Position = idDeltaPos + i * 2 }.ReadInt16();
            var idRangeOffsetAddress = idRangeOffsetPos + i * 2;
            var idRangeOffset = new BigEndianReader(cmap) { Position = idRangeOffsetAddress }.ReadUInt16();

            if (idRangeOffset == 0)
                return (ushort)((codepoint + idDelta) & 0xFFFF);

            var glyphIndexAddress = idRangeOffsetAddress + idRangeOffset + 2 * (codepoint - startCode);
            if (glyphIndexAddress + 2 > cmap.Length)
                return 0;
            var glyphId = new BigEndianReader(cmap) { Position = glyphIndexAddress }.ReadUInt16();
            return glyphId == 0 ? (ushort)0 : (ushort)((glyphId + idDelta) & 0xFFFF);
        }

        return 0;
    }

    private static ushort LookupFormat12(ReadOnlySpan<byte> cmap, int offset, int codepoint)
    {
        var header = new BigEndianReader(cmap) { Position = offset + 12 };
        var numGroups = header.ReadUInt32();
        var groupsPos = offset + 16;

        for (var i = 0; i < numGroups; i++) {
            var reader = new BigEndianReader(cmap) { Position = groupsPos + i * 12 };
            var startCharCode = reader.ReadUInt32();
            var endCharCode = reader.ReadUInt32();
            var startGlyphId = reader.ReadUInt32();

            if (codepoint >= startCharCode && codepoint <= endCharCode)
                return (ushort)(startGlyphId + ((uint)codepoint - startCharCode));
        }

        return 0;
    }
}

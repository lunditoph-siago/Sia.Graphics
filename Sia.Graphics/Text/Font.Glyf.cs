namespace Sia.Graphics.Text;

public sealed partial class Font
{
    private const int MaxCompositeDepth = 8;

    public GlyphOutline GetGlyphOutline(ushort glyphId)
    {
        var outline = new GlyphOutline { AdvanceWidth = GetAdvanceWidth(glyphId) };
        if (!TryGetTable("loca", out var loca) || !TryGetTable("glyf", out var glyf))
            return outline;

        var (start, end) = GetGlyfRange(loca, glyphId);
        if (end <= start)
            return outline; // no outline (e.g. space)

        AppendGlyph(glyf, start, end, outline, offsetX: 0f, offsetY: 0f, depth: 0);

        if (outline.Contours.Count > 0) {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (var contour in outline.Contours) {
                foreach (var point in contour.Points) {
                    minX = MathF.Min(minX, point.X);
                    minY = MathF.Min(minY, point.Y);
                    maxX = MathF.Max(maxX, point.X);
                    maxY = MathF.Max(maxY, point.Y);
                }
            }
            outline.MinX = minX;
            outline.MinY = minY;
            outline.MaxX = maxX;
            outline.MaxY = maxY;
        }

        return outline;
    }

    private (uint Start, uint End) GetGlyfRange(ReadOnlySpan<byte> loca, ushort glyphId)
    {
        if (glyphId >= NumGlyphs)
            return (0, 0);

        if (IndexToLocFormat == 0) {
            if ((glyphId + 2) * 2 > loca.Length)
                return (0, 0);
            var reader = new BigEndianReader(loca) { Position = glyphId * 2 };
            var start = (uint)reader.ReadUInt16() * 2;
            var end = (uint)reader.ReadUInt16() * 2;
            return (start, end);
        } else {
            if ((glyphId + 2) * 4 > loca.Length)
                return (0, 0);
            var reader = new BigEndianReader(loca) { Position = glyphId * 4 };
            var start = reader.ReadUInt32();
            var end = reader.ReadUInt32();
            return (start, end);
        }
    }

    private void AppendGlyph(
        ReadOnlySpan<byte> glyf, uint start, uint end, GlyphOutline outline,
        float offsetX, float offsetY, int depth)
    {
        if (depth > MaxCompositeDepth)
            return;
        if (start > int.MaxValue || end > (uint)glyf.Length || end - start < 10)
            return;

        var reader = new BigEndianReader(glyf) { Position = (int)start };
        var numberOfContours = reader.ReadInt16();
        reader.Position += 8; // xMin, yMin, xMax, yMax

        if (numberOfContours >= 0)
            AppendSimpleGlyph(ref reader, numberOfContours, outline, offsetX, offsetY);
        else
            AppendCompositeGlyph(glyf, ref reader, outline, offsetX, offsetY, depth);
    }

    private static void AppendSimpleGlyph(
        ref BigEndianReader reader, int numberOfContours, GlyphOutline outline, float offsetX, float offsetY)
    {
        var endPtsOfContours = new int[numberOfContours];
        for (var i = 0; i < numberOfContours; i++)
            endPtsOfContours[i] = reader.ReadUInt16();

        var numPoints = numberOfContours == 0 ? 0 : endPtsOfContours[^1] + 1;

        var instructionLength = reader.ReadUInt16();
        reader.Position += instructionLength;

        var flags = new byte[numPoints];
        for (var i = 0; i < numPoints;) {
            var flag = reader.ReadUInt8();
            flags[i++] = flag;
            if ((flag & 0x08) != 0) { // REPEAT_FLAG
                var repeatCount = reader.ReadUInt8();
                for (var r = 0; r < repeatCount && i < numPoints; r++)
                    flags[i++] = flag;
            }
        }

        var xs = new float[numPoints];
        var x = 0f;
        for (var i = 0; i < numPoints; i++) {
            var flag = flags[i];
            if ((flag & 0x02) != 0) { // X_SHORT_VECTOR
                var delta = reader.ReadUInt8();
                x += (flag & 0x10) != 0 ? delta : -delta;
            } else if ((flag & 0x10) == 0) { // not short, not "same" => signed 16-bit delta
                x += reader.ReadInt16();
            }
            xs[i] = x;
        }

        var ys = new float[numPoints];
        var y = 0f;
        for (var i = 0; i < numPoints; i++) {
            var flag = flags[i];
            if ((flag & 0x04) != 0) { // Y_SHORT_VECTOR
                var delta = reader.ReadUInt8();
                y += (flag & 0x20) != 0 ? delta : -delta;
            } else if ((flag & 0x20) == 0) {
                y += reader.ReadInt16();
            }
            ys[i] = y;
        }

        var pointIndex = 0;
        foreach (var endPt in endPtsOfContours) {
            var contour = new GlyphContour();
            for (; pointIndex <= endPt; pointIndex++) {
                var onCurve = (flags[pointIndex] & 0x01) != 0;
                contour.Points.Add(new GlyphPoint(xs[pointIndex] + offsetX, ys[pointIndex] + offsetY, onCurve));
            }
            outline.Contours.Add(contour);
        }
    }

    private void AppendCompositeGlyph(
        ReadOnlySpan<byte> glyf, ref BigEndianReader reader, GlyphOutline outline,
        float offsetX, float offsetY, int depth)
    {
        const ushort argsAreWords = 0x0001;
        const ushort argsAreXy = 0x0002;
        const ushort haveScale = 0x0008;
        const ushort moreComponents = 0x0020;
        const ushort haveXyScale = 0x0040;
        const ushort haveTwoByTwo = 0x0080;

        while (true) {
            var flags = reader.ReadUInt16();
            var componentGlyphId = reader.ReadUInt16();

            float dx = 0f, dy = 0f;
            if ((flags & argsAreWords) != 0) {
                var a1 = reader.ReadInt16();
                var a2 = reader.ReadInt16();
                if ((flags & argsAreXy) != 0) { dx = a1; dy = a2; }
            } else {
                var a1 = reader.ReadInt8();
                var a2 = reader.ReadInt8();
                if ((flags & argsAreXy) != 0) { dx = a1; dy = a2; }
            }

            if ((flags & haveScale) != 0) {
                reader.ReadInt16();
            } else if ((flags & haveXyScale) != 0) {
                reader.ReadInt16();
                reader.ReadInt16();
            } else if ((flags & haveTwoByTwo) != 0) {
                reader.ReadInt16();
                reader.ReadInt16();
                reader.ReadInt16();
                reader.ReadInt16();
            }

            if (TryGetTable("loca", out var loca)) {
                var (start, end) = GetGlyfRange(loca, componentGlyphId);
                if (end > start)
                    AppendGlyph(glyf, start, end, outline, offsetX + dx, offsetY + dy, depth + 1);
            }

            if ((flags & moreComponents) == 0)
                break;
        }
    }
}

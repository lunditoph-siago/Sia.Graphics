namespace Sia.Graphics.Text;

public readonly record struct RasterizedGlyph(byte[] Coverage, int Width, int Height, float OriginX, float OriginY);

public static class GlyphRasterizer
{
    private const int CurveSegments = 8;
    private const int VerticalSamples = 4;

    public static RasterizedGlyph Rasterize(GlyphOutline outline, float unitsPerEm, float pixelSize)
    {
        var scale = unitsPerEm > 0f ? pixelSize / unitsPerEm : 0f;

        if (outline.Contours.Count == 0 || scale <= 0f)
            return new RasterizedGlyph([], 0, 0, 0f, 0f);

        var polygons = new List<(float X, float Y)[]>(outline.Contours.Count);
        foreach (var contour in outline.Contours) {
            var polygon = FlattenContour(contour, scale);
            if (polygon.Length >= 3)
                polygons.Add(polygon);
        }
        if (polygons.Count == 0)
            return new RasterizedGlyph([], 0, 0, 0f, 0f);

        var minX = outline.MinX * scale;
        var minY = outline.MinY * scale;
        var maxX = outline.MaxX * scale;
        var maxY = outline.MaxY * scale;

        var originX = MathF.Floor(minX) - 1f;
        var originY = MathF.Floor(minY) - 1f;
        var width = (int)MathF.Ceiling(maxX) - (int)originX + 1;
        var height = (int)MathF.Ceiling(maxY) - (int)originY + 1;
        if (width <= 0 || height <= 0)
            return new RasterizedGlyph([], 0, 0, 0f, 0f);

        for (var p = 0; p < polygons.Count; p++) {
            var polygon = polygons[p];
            for (var i = 0; i < polygon.Length; i++)
                polygon[i] = (polygon[i].X - originX, polygon[i].Y - originY);
        }

        var coverage = RasterizeCoverage(polygons, width, height);
        FlipVertically(coverage, width, height);
        var topOffset = -MathF.Ceiling(maxY) - 1f;
        return new RasterizedGlyph(coverage, width, height, originX, topOffset);
    }

    private static (float X, float Y)[] FlattenContour(GlyphContour contour, float scale)
    {
        var points = contour.Points;
        if (points.Count == 0)
            return [];
        if (points.Count == 1)
            return [(points[0].X * scale, points[0].Y * scale)];

        var startIndex = points.FindIndex(p => p.OnCurve);
        GlyphPoint start;
        int firstOffCurveIndex;
        if (startIndex < 0) {
            start = Midpoint(points[0], points[1]);
            firstOffCurveIndex = 0;
        }
        else {
            start = points[startIndex];
            firstOffCurveIndex = (startIndex + 1) % points.Count;
        }

        var result = new List<(float, float)> { (start.X * scale, start.Y * scale) };
        var current = start;
        var i = firstOffCurveIndex;
        var visited = 0;
        var n = points.Count;

        while (visited < n) {
            var point = points[i];
            if (point.OnCurve) {
                result.Add((point.X * scale, point.Y * scale));
                current = point;
                i = (i + 1) % n;
                visited++;
            }
            else {
                var next = points[(i + 1) % n];
                GlyphPoint end;
                var consumedNext = false;
                if (next.OnCurve) {
                    end = next;
                    consumedNext = true;
                }
                else {
                    end = Midpoint(point, next);
                }
                AppendQuadratic(result, current, point, end, scale);
                current = end;
                i = (i + 1) % n;
                visited++;
                if (consumedNext) {
                    i = (i + 1) % n;
                    visited++;
                }
            }
        }

        return [.. result];
    }

    private static GlyphPoint Midpoint(GlyphPoint a, GlyphPoint b) =>
        new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f, true);

    private static void AppendQuadratic(
        List<(float X, float Y)> result, GlyphPoint start, GlyphPoint control, GlyphPoint end, float scale)
    {
        for (var s = 1; s <= CurveSegments; s++) {
            var t = (float)s / CurveSegments;
            var mt = 1f - t;
            var x = mt * mt * start.X + 2f * mt * t * control.X + t * t * end.X;
            var y = mt * mt * start.Y + 2f * mt * t * control.Y + t * t * end.Y;
            result.Add((x * scale, y * scale));
        }
    }

    private static byte[] RasterizeCoverage(List<(float X, float Y)[]> polygons, int width, int height)
    {
        var coverage = new byte[width * height];
        var accum = new float[width];
        var crossings = new List<(float X, int Direction)>();
        var sampleWeight = 1f / VerticalSamples;

        for (var row = 0; row < height; row++) {
            Array.Clear(accum);

            for (var s = 0; s < VerticalSamples; s++) {
                var sampleY = row + (s + 0.5f) / VerticalSamples;
                crossings.Clear();

                foreach (var polygon in polygons) {
                    for (var i = 0; i < polygon.Length; i++) {
                        var a = polygon[i];
                        var b = polygon[(i + 1) % polygon.Length];
                        if (a.Y == b.Y)
                            continue;
                        var (lo, hi, dir) = a.Y < b.Y ? (a, b, 1) : (b, a, -1);
                        if (sampleY < lo.Y || sampleY >= hi.Y)
                            continue;
                        var t = (sampleY - lo.Y) / (hi.Y - lo.Y);
                        var x = lo.X + t * (hi.X - lo.X);
                        crossings.Add((x, dir));
                    }
                }

                if (crossings.Count == 0)
                    continue;
                crossings.Sort((c1, c2) => c1.X.CompareTo(c2.X));

                var winding = 0;
                for (var c = 0; c < crossings.Count - 1; c++) {
                    winding += crossings[c].Direction;
                    if (winding == 0)
                        continue;
                    AccumulateSpan(accum, crossings[c].X, crossings[c + 1].X, sampleWeight, width);
                }
            }

            for (var x = 0; x < width; x++)
                coverage[row * width + x] = (byte)System.Math.Clamp(MathF.Round(accum[x] * 255f), 0f, 255f);
        }

        return coverage;
    }

    private static void AccumulateSpan(float[] accum, float spanStart, float spanEnd, float weight, int width)
    {
        spanStart = System.Math.Clamp(spanStart, 0f, width);
        spanEnd = System.Math.Clamp(spanEnd, 0f, width);
        if (spanEnd <= spanStart)
            return;

        var startPixel = (int)MathF.Floor(spanStart);
        var endPixel = (int)MathF.Floor(spanEnd);

        if (startPixel == endPixel) {
            if (startPixel < width)
                accum[startPixel] += weight * (spanEnd - spanStart);
            return;
        }

        if (startPixel < width)
            accum[startPixel] += weight * (startPixel + 1 - spanStart);
        for (var x = startPixel + 1; x < endPixel && x < width; x++)
            accum[x] += weight;
        if (endPixel < width && endPixel >= 0)
            accum[endPixel] += weight * (spanEnd - endPixel);
    }

    private static void FlipVertically(byte[] pixels, int width, int height)
    {
        for (var top = 0; top < height / 2; top++) {
            var bottom = height - 1 - top;
            for (var x = 0; x < width; x++)
                (pixels[top * width + x], pixels[bottom * width + x]) =
                    (pixels[bottom * width + x], pixels[top * width + x]);
        }
    }
}

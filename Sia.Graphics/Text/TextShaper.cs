using System.Text;

namespace Sia.Graphics.Text;

public readonly record struct TextPoint(float X, float Y);

public readonly record struct TextSize(float Width, float Height);

public readonly record struct ShapedGlyph(ushort GlyphId, TextPoint Position)
{
    public Font? Font { get; init; }
    public int Codepoint { get; init; }
    public bool UsedFallback { get; init; }
}

public sealed class ShapedText
{
    public List<ShapedGlyph> Glyphs { get; } = [];
    public TextSize Size { get; set; }
    public float Baseline { get; set; }
    public float LineHeight { get; set; }
}

public static class TextShaper
{
    public static ShapedText Shape(string text, Font font, float fontSize, float? availableWidth) =>
        Shape(text, font, [], fontSize, availableWidth);

    public static ShapedText Shape(
        string text,
        Font font,
        IReadOnlyList<Font> fallbackFonts,
        float fontSize,
        float? availableWidth)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(fallbackFonts);
        if (!float.IsFinite(fontSize) || fontSize < 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));

        var fonts = new Font[fallbackFonts.Count + 1];
        fonts[0] = font;
        for (var i = 0; i < fallbackFonts.Count; i++)
            fonts[i + 1] = fallbackFonts[i];

        var ascender = 0f;
        var descender = 0f;
        foreach (var candidate in fonts) {
            var scale = Scale(candidate, fontSize);
            ascender = MathF.Max(ascender, candidate.Ascender * scale);
            descender = MathF.Min(descender, candidate.Descender * scale);
        }
        var lineHeight = ascender - descender;
        if (lineHeight <= 0f)
            lineHeight = fontSize * 1.2f;
        var baseline = ascender > 0f ? ascender : lineHeight * 0.8f;

        var glyphs = ResolveGlyphs(text, fonts, fontSize);
        var lines = Wrap(glyphs, availableWidth);
        var result = new ShapedText { Baseline = baseline, LineHeight = lineHeight };
        var maxWidth = 0f;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++) {
            var (start, count) = lines[lineIndex];
            var cursorX = 0f;
            var baselineY = baseline + lineIndex * lineHeight;
            for (var glyphIndex = 0; glyphIndex < count; glyphIndex++) {
                var glyph = glyphs[start + glyphIndex]!.Value;
                if (glyphIndex > 0)
                    cursorX += glyph.KerningBefore;
                result.Glyphs.Add(new ShapedGlyph(glyph.GlyphId, new TextPoint(cursorX, baselineY)) {
                    Font = glyph.Font,
                    Codepoint = glyph.Rune.Value,
                    UsedFallback = !ReferenceEquals(glyph.Font, font)
                });
                cursorX += glyph.Advance;
            }
            maxWidth = MathF.Max(maxWidth, cursorX);
        }

        result.Size = new TextSize(maxWidth, lines.Count * lineHeight);
        return result;
    }

    private static List<ResolvedGlyph?> ResolveGlyphs(string text, IReadOnlyList<Font> fonts, float fontSize)
    {
        var result = new List<ResolvedGlyph?>();
        Font? previousFont = null;
        ushort previousGlyphId = 0;
        foreach (var rune in text.EnumerateRunes()) {
            if (rune.Value == '\r')
                continue;
            if (rune.Value == '\n') {
                result.Add(null);
                previousFont = null;
                previousGlyphId = 0;
                continue;
            }

            var selected = fonts[0];
            var glyphId = selected.GetGlyphIndex(rune.Value);
            if (glyphId == 0) {
                for (var i = 1; i < fonts.Count; i++) {
                    var fallbackGlyph = fonts[i].GetGlyphIndex(rune.Value);
                    if (fallbackGlyph == 0)
                        continue;
                    selected = fonts[i];
                    glyphId = fallbackGlyph;
                    break;
                }
            }
            var advance = selected.GetAdvanceWidth(glyphId) * Scale(selected, fontSize);
            var kerning = ReferenceEquals(previousFont, selected)
                ? selected.GetKerning(previousGlyphId, glyphId) * Scale(selected, fontSize)
                : 0f;
            result.Add(new ResolvedGlyph(rune, selected, glyphId, advance, kerning));
            previousFont = selected;
            previousGlyphId = glyphId;
        }
        return result;
    }

    private static List<(int Start, int Count)> Wrap(
        IReadOnlyList<ResolvedGlyph?> glyphs, float? availableWidth)
    {
        var lines = new List<(int Start, int Count)>();
        var start = 0;
        for (var index = 0; index < glyphs.Count; index++) {
            if (glyphs[index] is not null) {
                continue;
            }
            AppendWrappedParagraph(lines, glyphs, start, index, availableWidth);
            start = index + 1;
        }
        AppendWrappedParagraph(lines, glyphs, start, glyphs.Count, availableWidth);
        return lines;
    }

    private static void AppendWrappedParagraph(
        List<(int Start, int Count)> lines,
        IReadOnlyList<ResolvedGlyph?> glyphs,
        int start,
        int paragraphEnd,
        float? availableWidth)
    {
        if (availableWidth is not { } limit || !float.IsFinite(limit) || limit <= 0f) {
            lines.Add((start, paragraphEnd - start));
            return;
        }
        if (start == paragraphEnd) {
            lines.Add((start, 0));
            return;
        }

        while (start < paragraphEnd) {
            var width = 0f;
            var lastBreak = -1;
            var end = start;
            for (; end < paragraphEnd; end++) {
                var glyph = glyphs[end]!.Value;
                var kerning = end == start ? 0f : glyph.KerningBefore;
                if (width + kerning + glyph.Advance > limit && end > start)
                    break;
                width += kerning + glyph.Advance;
                if (Rune.IsWhiteSpace(glyph.Rune))
                    lastBreak = end + 1;
            }

            if (end == paragraphEnd) {
                lines.Add((start, end - start));
                break;
            }
            var lineEnd = lastBreak > start ? lastBreak : end;
            lines.Add((start, lineEnd - start));
            start = lineEnd;
            while (start < paragraphEnd && Rune.IsWhiteSpace(glyphs[start]!.Value.Rune))
                start++;
        }
    }

    private static float Scale(Font font, float fontSize) =>
        font.UnitsPerEm > 0 ? fontSize / font.UnitsPerEm : 0f;

    private readonly record struct ResolvedGlyph(
        Rune Rune,
        Font Font,
        ushort GlyphId,
        float Advance,
        float KerningBefore);
}

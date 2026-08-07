using Sia;

namespace Sia.Graphics.Text;

public sealed class FontAtlasSet : IAddon
{
    private const int MinAtlasSize = 256;

    private readonly Dictionary<FontAtlasKey, List<FontAtlas>> _atlases = [];

    public GlyphAtlasInfo GetOrAddGlyph(Font font, float pixelSize, ushort glyphId)
    {
        var key = FontAtlasKey.For(font, pixelSize);
        var atlases = _atlases.TryGetValue(key, out var existing) ? existing : _atlases[key] = [];

        foreach (var atlas in atlases) {
            if (atlas.TryGetGlyph(glyphId, out var location))
                return new GlyphAtlasInfo(atlas, location);
        }

        var glyph = GlyphRasterizer.Rasterize(font.GetGlyphOutline(glyphId), font.UnitsPerEm, pixelSize);

        foreach (var atlas in atlases) {
            if (atlas.TryAddGlyph(glyphId, glyph, out var location))
                return new GlyphAtlasInfo(atlas, location);
        }

        var size = Math.Max(MinAtlasSize, NextPowerOfTwo(Math.Max(glyph.Width, glyph.Height) * 4));
        var newAtlas = new FontAtlas(size, size);
        atlases.Add(newAtlas);

        if (!newAtlas.TryAddGlyph(glyphId, glyph, out var newLocation)) {
            newLocation = new GlyphAtlasLocation(0, 0, 0, 0, glyph.OriginX, glyph.OriginY);
        }
        return new GlyphAtlasInfo(newAtlas, newLocation);
    }

    private static int NextPowerOfTwo(int value)
    {
        var power = 1;
        while (power < value)
            power *= 2;
        return power;
    }
}

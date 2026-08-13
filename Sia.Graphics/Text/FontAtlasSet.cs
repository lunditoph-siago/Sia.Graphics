using Sia;

namespace Sia.Graphics.Text;

public sealed class FontAtlasSet : IAddon
{
    internal const int AtlasSize = 1024;
    internal const int MaxAtlasLayers = 64;

    private readonly Dictionary<FontAtlasKey, List<FontAtlas>> _atlases = [];
    private readonly List<FontAtlas> _allAtlases = [];
    internal IReadOnlyList<FontAtlas> Atlases => _allAtlases;

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

        if (_allAtlases.Count >= MaxAtlasLayers - 1)
            throw new InvalidOperationException("The UI font texture array is full.");
        var newAtlas = new FontAtlas(AtlasSize, AtlasSize, _allAtlases.Count + 1);
        atlases.Add(newAtlas);
        _allAtlases.Add(newAtlas);

        if (!newAtlas.TryAddGlyph(glyphId, glyph, out var newLocation)) {
            newLocation = new GlyphAtlasLocation(0, 0, 0, 0, glyph.OriginX, glyph.OriginY);
        }
        return new GlyphAtlasInfo(newAtlas, newLocation);
    }

}

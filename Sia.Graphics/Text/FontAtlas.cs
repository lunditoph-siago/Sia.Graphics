namespace Sia.Graphics.Text;

public sealed class FontAtlas
{
    private readonly DynamicTextureAtlasBuilder _packer;
    private readonly Dictionary<ushort, GlyphAtlasLocation> _glyphs = [];
    private readonly byte[] _pixels;
    private int _dirtyLeft = int.MaxValue;
    private int _dirtyTop = int.MaxValue;
    private int _dirtyRight;
    private int _dirtyBottom;

    public int Width { get; }
    public int Height { get; }
    internal int Layer { get; }
    internal byte[] Pixels => _pixels;

    internal FontAtlas(int width, int height, int layer)
    {
        Width = width;
        Height = height;
        Layer = layer;
        _packer = new DynamicTextureAtlasBuilder(width, height);
        _pixels = new byte[width * height * 4];
    }

    public bool TryGetGlyph(ushort glyphId, out GlyphAtlasLocation location) =>
        _glyphs.TryGetValue(glyphId, out location);

    public bool TryAddGlyph(ushort glyphId, RasterizedGlyph glyph, out GlyphAtlasLocation location)
    {
        if (glyph.Width == 0 || glyph.Height == 0) {
            location = new GlyphAtlasLocation(0, 0, 0, 0, glyph.OriginX, glyph.OriginY);
            _glyphs[glyphId] = location;
            return true;
        }

        if (!_packer.TryAllocate(glyph.Width, glyph.Height, out var x, out var y)) {
            location = default;
            return false;
        }

        for (var row = 0; row < glyph.Height; row++) {
            for (var col = 0; col < glyph.Width; col++) {
                var coverage = glyph.Coverage[row * glyph.Width + col];
                var dst = ((y + row) * Width + (x + col)) * 4;
                _pixels[dst + 0] = 255;
                _pixels[dst + 1] = 255;
                _pixels[dst + 2] = 255;
                _pixels[dst + 3] = coverage;
            }
        }
        _dirtyLeft = Math.Min(_dirtyLeft, x);
        _dirtyTop = Math.Min(_dirtyTop, y);
        _dirtyRight = Math.Max(_dirtyRight, x + glyph.Width);
        _dirtyBottom = Math.Max(_dirtyBottom, y + glyph.Height);

        location = new GlyphAtlasLocation(x, y, glyph.Width, glyph.Height, glyph.OriginX, glyph.OriginY);
        _glyphs[glyphId] = location;
        return true;
    }

    internal bool TryTakeDirtyRegion(out FontAtlasDirtyRegion region)
    {
        if (_dirtyLeft == int.MaxValue) {
            region = default;
            return false;
        }
        region = new FontAtlasDirtyRegion(
            _dirtyLeft,
            _dirtyTop,
            _dirtyRight - _dirtyLeft,
            _dirtyBottom - _dirtyTop);
        _dirtyLeft = int.MaxValue;
        _dirtyTop = int.MaxValue;
        _dirtyRight = 0;
        _dirtyBottom = 0;
        return true;
    }
}

internal readonly record struct FontAtlasDirtyRegion(int X, int Y, int Width, int Height);

using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

public readonly record struct PositionedGlyph(
    Point Position,
    GlyphAtlasInfo AtlasInfo,
    int Codepoint,
    ushort GlyphId,
    bool UsedFallback);

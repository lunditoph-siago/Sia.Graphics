namespace Sia.Graphics.UI;

public record struct TextLayoutInfo
{
    public List<PositionedGlyph> Glyphs = [];

    public TextLayoutInfo() { }
}

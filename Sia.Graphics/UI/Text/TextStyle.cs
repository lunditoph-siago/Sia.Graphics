using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

public record struct TextStyle(Font Font, float FontSize, Color Color)
{
    public IReadOnlyList<Font> FallbackFonts { get; init; } = [];
    public ITextShapingProvider? ShapingProvider { get; init; }
}

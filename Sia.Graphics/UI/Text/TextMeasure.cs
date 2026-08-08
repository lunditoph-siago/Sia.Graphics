using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

public sealed class TextMeasure(
    Font font,
    IReadOnlyList<Font> fallbackFonts,
    ITextShapingProvider? shapingProvider,
    float fontSize,
    string text) : ILayoutMeasure
{
    public ShapedText? LastShaped { get; private set; }
    public float? Baseline => LastShaped?.Baseline;

    public Size Measure(PartialSize knownDimensions, AvailableSize availableSpace)
    {
        var availableWidth = knownDimensions.Width
            ?? (availableSpace.Width.IsDefinite ? availableSpace.Width.Value : (float?)null);
        var shaped = shapingProvider?.Shape(text, font, fallbackFonts, fontSize, availableWidth)
            ?? TextShaper.Shape(text, font, fallbackFonts, fontSize, availableWidth);
        LastShaped = shaped;
        return shaped.Size;
    }
}

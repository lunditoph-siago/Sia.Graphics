using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

public sealed class TextMeasure(
    Font font,
    IReadOnlyList<Font> fallbackFonts,
    ITextShapingProvider? shapingProvider,
    float fontSize,
    string text) : ILayoutMeasure
{
    private float? _lastAvailableWidth;
    private Size _lastSize;
    private bool _hasMeasurement;

    public ShapedText? LastShaped { get; private set; }
    public float? Baseline => LastShaped?.Baseline;

    public Size Measure(PartialSize knownDimensions, AvailableSize availableSpace)
    {
        var availableWidth = knownDimensions.Width
            ?? (availableSpace.Width.IsDefinite ? availableSpace.Width.Value : (float?)null);
        if (_hasMeasurement && _lastAvailableWidth == availableWidth) {
            return _lastSize;
        }
        var shaped = shapingProvider?.Shape(text, font, fallbackFonts, fontSize, availableWidth)
            ?? TextShaper.Shape(text, font, fallbackFonts, fontSize, availableWidth);
        LastShaped = shaped;
        _lastAvailableWidth = availableWidth;
        _lastSize = shaped.Size;
        _hasMeasurement = true;
        return _lastSize;
    }
}

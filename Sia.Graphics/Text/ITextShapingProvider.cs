namespace Sia.Graphics.Text;

public interface ITextShapingProvider
{
    public ShapedText Shape(
        string text,
        Font primaryFont,
        IReadOnlyList<Font> fallbackFonts,
        float fontSize,
        float? availableWidth);
}

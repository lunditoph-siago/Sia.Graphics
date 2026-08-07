namespace Sia.Graphics.Text;

public readonly record struct FontAtlasKey(Font Font, int PixelSize)
{
    public static FontAtlasKey For(Font font, float pixelSize) => new(font, (int)MathF.Round(pixelSize));
}

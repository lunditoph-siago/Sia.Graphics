namespace Sia.Graphics.UI;

public readonly record struct Color(float R, float G, float B, float A)
{
    public static readonly Color Transparent = new(0f, 0f, 0f, 0f);
    public static readonly Color White = new(1f, 1f, 1f, 1f);
    public static readonly Color Black = new(0f, 0f, 0f, 1f);

    public static Color Rgb(float r, float g, float b) => new(r, g, b, 1f);
}

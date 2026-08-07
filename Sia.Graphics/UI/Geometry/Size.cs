namespace Sia.Graphics.UI;

public readonly record struct Size(float Width, float Height)
{
    public static readonly Size Zero = new(0f, 0f);

    public static Size operator +(Size a, Size b) => new(a.Width + b.Width, a.Height + b.Height);
    public static Size operator -(Size a, Size b) => new(a.Width - b.Width, a.Height - b.Height);

    public float this[UiAxis axis] => axis == UiAxis.Horizontal ? Width : Height;

    public Size WithAxis(UiAxis axis, float value) =>
        axis == UiAxis.Horizontal ? this with { Width = value } : this with { Height = value };
}

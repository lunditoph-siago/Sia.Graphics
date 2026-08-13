namespace Sia.Graphics.UI;

public readonly record struct BorderEdges(float Left, float Right, float Top, float Bottom)
{
    public static readonly BorderEdges Zero = new(0f, 0f, 0f, 0f);
}

public readonly record struct ResolvedBorderRadius(float TopLeft, float TopRight, float BottomRight, float BottomLeft)
{
    public static readonly ResolvedBorderRadius Zero = new(0f, 0f, 0f, 0f);
}

public readonly record struct UiClipRect(float X, float Y, float Width, float Height)
{
    private const float UnboundedExtent = 1e9f;

    internal static readonly UiClipRect Unbounded = new(
        -UnboundedExtent,
        -UnboundedExtent,
        UnboundedExtent * 2f,
        UnboundedExtent * 2f);

    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(Point point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    public UiClipRect Intersect(UiClipRect other)
    {
        var x = MathF.Max(X, other.X);
        var y = MathF.Max(Y, other.Y);
        var right = MathF.Min(Right, other.Right);
        var bottom = MathF.Min(Bottom, other.Bottom);
        return new UiClipRect(x, y, MathF.Max(0f, right - x), MathF.Max(0f, bottom - y));
    }
}

public record struct ComputedNode
{
    public Size Size;
    public Size UnroundedSize;
    public Size ContentSize;

    public BorderEdges Border;
    public BorderEdges Padding;
    public ResolvedBorderRadius BorderRadius;
    public UiClipRect? ClipRect;

    public Size ScrollbarSize;
    public Size ScrollPosition;

    public float OutlineWidth;
    public float OutlineOffset;

    public float InverseScaleFactor;

    public int StackIndex;

    public ComputedNode()
    {
        InverseScaleFactor = 1f;
    }

    public readonly bool ContainsPoint(Point localPoint) =>
        localPoint.X >= 0f && localPoint.X <= Size.Width &&
        localPoint.Y >= 0f && localPoint.Y <= Size.Height;
}

public readonly record struct Point(float X, float Y)
{
    public static readonly Point Zero = new(0f, 0f);

    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
}

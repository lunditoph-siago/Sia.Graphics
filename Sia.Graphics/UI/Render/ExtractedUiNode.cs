namespace Sia.Graphics.UI;

public enum ExtractedUiNodeKind
{
    Background,
    Border
}

public readonly record struct ExtractedUiNode(
    Point TopLeft,
    Size Size,
    Color Color,
    ResolvedBorderRadius BorderRadius,
    BorderEdges Border,
    ExtractedUiNodeKind Kind,
    int StackIndex,
    object? TextureKey,
    Point UvMin,
    Point UvMax,
    UiClipRect? ClipRect = null,
    UiGlobalTransform? Transform = null)
{
    public static ExtractedUiNode SolidColor(
        Point topLeft, Size size, Color color, ResolvedBorderRadius radius, BorderEdges border,
        ExtractedUiNodeKind kind, int stackIndex) =>
        new(topLeft, size, color, radius, border, kind, stackIndex, null, Point.Zero, new Point(1f, 1f));
}

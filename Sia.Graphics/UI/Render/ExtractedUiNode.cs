namespace Sia.Graphics.UI;

internal enum ExtractedUiNodeKind
{
    Background,
    Border
}

internal readonly record struct ExtractedUiNode(
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
    UiGlobalTransform? Transform = null,
    int SubOrder = 0)
{
    public static ExtractedUiNode SolidColor(
        Point topLeft, Size size, Color color, ResolvedBorderRadius radius, BorderEdges border,
        ExtractedUiNodeKind kind, int stackIndex) =>
        new(topLeft, size, color, radius, border, kind, stackIndex, null, Point.Zero, new Point(1f, 1f));
}

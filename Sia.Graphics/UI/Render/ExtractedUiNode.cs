namespace Sia.Graphics.UI;

internal readonly record struct ExtractedUiNode(
    Entity Owner,
    Point TopLeft,
    Size Size,
    Color Color,
    ResolvedBorderRadius BorderRadius,
    BorderEdges Border,
    int StackIndex,
    object? TextureKey,
    Point UvMin,
    UiClipRect? ClipRect = null,
    UiGlobalTransform? Transform = null,
    int SubOrder = 0)
{
    public static ExtractedUiNode SolidColor(
        Entity owner, Point topLeft, Size size, Color color, ResolvedBorderRadius radius,
        BorderEdges border, int stackIndex) =>
        new(owner, topLeft, size, color, radius, border, stackIndex, null, Point.Zero);
}

namespace Sia.Graphics.UI;

internal static class UiPrimitiveBuilder
{
    public static void Build(IReadOnlyList<ExtractedUiNode> nodes, List<UiPrimitive> primitives)
    {
        primitives.Clear();
        primitives.EnsureCapacity(nodes.Count);
        foreach (var node in nodes) {
            if (node.Size.Width <= 0f || node.Size.Height <= 0f
                || !IsVisible(node)) {
                continue;
            }
            primitives.Add(UiPrimitive.Create(node));
        }
    }

    private static bool IsVisible(in ExtractedUiNode node)
    {
        if (node.ClipRect is not { } clip)
            return true;
        if (clip.Width <= 0f || clip.Height <= 0f)
            return false;

        var transform = node.Transform ?? UiGlobalTransform.Identity;
        var topLeft = transform.Transform(node.TopLeft);
        var topRight = transform.Transform(new Point(node.TopLeft.X + node.Size.Width, node.TopLeft.Y));
        var bottomLeft = transform.Transform(new Point(node.TopLeft.X, node.TopLeft.Y + node.Size.Height));
        var bottomRight = transform.Transform(new Point(
            node.TopLeft.X + node.Size.Width,
            node.TopLeft.Y + node.Size.Height));
        var left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        var right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        var top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        var bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return right > clip.X && left < clip.Right && bottom > clip.Y && top < clip.Bottom;
    }
}

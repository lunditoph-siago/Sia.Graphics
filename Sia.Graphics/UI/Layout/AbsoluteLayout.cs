namespace Sia.Graphics.UI;

internal static class AbsoluteLayout
{
    public static void ComputeChildren(LayoutTree tree, LayoutNodeId parent, LayoutInput input)
    {
        ref readonly var parentLayout = ref tree.GetLayout(parent);
        var containingWidth = parentLayout.ContentSize.Width;
        var containingHeight = parentLayout.ContentSize.Height;

        foreach (var child in tree.GetChildren(parent)) {
            var style = tree.GetStyle(child);
            if (style.Display == Display.None || style.PositionType != PositionType.Absolute)
                continue;

            var margin = EdgeResolution.Resolve(style.Margin, input, containingWidth);
            var border = EdgeResolution.Resolve(style.Border, input, containingWidth);
            var padding = EdgeResolution.Resolve(style.Padding, input, containingWidth);
            var extraWidth = border.Left + border.Right + padding.Left + padding.Right;
            var extraHeight = border.Top + border.Bottom + padding.Top + padding.Bottom;

            var left = input.Resolve(style.Left, containingWidth);
            var right = input.Resolve(style.Right, containingWidth);
            var top = input.Resolve(style.Top, containingHeight);
            var bottom = input.Resolve(style.Bottom, containingHeight);

            var width = EdgeResolution.ResolveBorderBoxSize(
                style, null, style.Width, input, containingWidth, extraWidth);
            var height = EdgeResolution.ResolveBorderBoxSize(
                style, null, style.Height, input, containingHeight, extraHeight);
            if (width == null && left != null && right != null) {
                width = MathF.Max(0f, containingWidth - left.Value - right.Value
                    - margin.Left - margin.Right);
            }
            if (height == null && top != null && bottom != null) {
                height = MathF.Max(0f, containingHeight - top.Value - bottom.Value
                    - margin.Top - margin.Bottom);
            }

            var childInput = input with {
                KnownDimensions = new PartialSize(width, height),
                ParentSize = new PartialSize(containingWidth, containingHeight),
                AvailableSpace = new AvailableSize(
                    width is { } resolvedWidth
                        ? AvailableSpace.Definite(resolvedWidth)
                        : AvailableSpace.MaxContent,
                    height is { } resolvedHeight
                        ? AvailableSpace.Definite(resolvedHeight)
                        : AvailableSpace.MaxContent),
                PerformLayout = true
            };
            var childSize = tree.ComputeNodeSize(child, childInput);

            var x = left is { } resolvedLeft
                ? resolvedLeft + margin.Left
                : right is { } resolvedRight
                    ? containingWidth - resolvedRight - childSize.Width - margin.Right
                    : margin.Left;
            var y = top is { } resolvedTop
                ? resolvedTop + margin.Top
                : bottom is { } resolvedBottom
                    ? containingHeight - resolvedBottom - childSize.Height - margin.Bottom
                    : margin.Top;

            var layout = tree.GetLayout(child);
            layout.Location = new Point(x, y);
            tree.SetChildLayout(child, layout);
        }
    }
}

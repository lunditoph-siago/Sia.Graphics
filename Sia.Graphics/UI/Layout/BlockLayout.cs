namespace Sia.Graphics.UI;

internal static class BlockLayout
{
    public static Size Compute(LayoutTree tree, LayoutNodeId id, LayoutInput input)
    {
        var style = tree.GetStyle(id);
        var scale = input.ScaleFactor;

        var parentWidth = input.ParentSize.Width ?? input.AvailableSpace.Width.UnwrapOr(0f);
        var parentHeight = input.ParentSize.Height ?? input.AvailableSpace.Height.UnwrapOr(0f);

        var border = EdgeResolution.Resolve(style.Border, input, parentWidth);
        var padding = EdgeResolution.Resolve(style.Padding, input, parentWidth);
        var extraWidth = border.Left + border.Right + padding.Left + padding.Right;
        var extraHeight = border.Top + border.Bottom + padding.Top + padding.Bottom;

        var explicitWidth = EdgeResolution.ResolveBorderBoxSize(
            style, input.KnownDimensions.Width, style.Width, input, parentWidth, extraWidth);
        var explicitHeight = EdgeResolution.ResolveBorderBoxSize(
            style, input.KnownDimensions.Height, style.Height, input, parentHeight, extraHeight);
        EdgeResolution.ApplyAspectRatio(style, ref explicitWidth, ref explicitHeight);
        var contentWidth = explicitWidth is { } ew
            ? MathF.Max(0f, ew - extraWidth)
            : MathF.Max(0f, input.AvailableSpace.Width.UnwrapOr(0f) - extraWidth);

        var children = tree.GetChildren(id);
        var cursorY = 0f;
        var maxChildWidth = 0f;

        foreach (var child in children) {
            var childStyle = tree.GetStyle(child);
            if (childStyle.Display == Display.None || childStyle.PositionType == PositionType.Absolute)
                continue;

            var childMargin = EdgeResolution.Resolve(childStyle.Margin, input, contentWidth);
            var childInput = input with {
                KnownDimensions = new PartialSize(
                    MathF.Max(0f, contentWidth - childMargin.Left - childMargin.Right), null),
                ParentSize = new PartialSize(contentWidth, null),
                AvailableSpace = new AvailableSize(
                    AvailableSpace.Definite(MathF.Max(0f, contentWidth - childMargin.Left - childMargin.Right)),
                    input.AvailableSpace.Height)
            };

            var childSize = tree.ComputeNodeSize(child, childInput);

            if (input.PerformLayout) {
                var childLayout = tree.GetLayout(child);
                childLayout.Location = new Point(childMargin.Left, cursorY + childMargin.Top);
                childLayout.Size = childSize;
                tree.SetChildLayout(child, childLayout);
            }

            cursorY += childMargin.Top + childSize.Height + childMargin.Bottom;
            maxChildWidth = MathF.Max(maxChildWidth, childSize.Width + childMargin.Left + childMargin.Right);
        }

        var contentHeight = explicitHeight is { } eh ? MathF.Max(0f, eh - extraHeight) : cursorY;
        var resolvedContentWidth = explicitWidth is { } ? contentWidth : MathF.Max(contentWidth, maxChildWidth);

        var minWidth = EdgeResolution.ResolveContentConstraint(style, style.MinWidth, input, parentWidth, extraWidth);
        var maxWidth = EdgeResolution.ResolveContentConstraint(style, style.MaxWidth, input, parentWidth, extraWidth);
        var minHeight = EdgeResolution.ResolveContentConstraint(style, style.MinHeight, input, parentHeight, extraHeight);
        var maxHeight = EdgeResolution.ResolveContentConstraint(style, style.MaxHeight, input, parentHeight, extraHeight);

        resolvedContentWidth = EdgeResolution.Clamp(resolvedContentWidth, minWidth, maxWidth);
        contentHeight = EdgeResolution.Clamp(contentHeight, minHeight, maxHeight);

        var size = new Size(resolvedContentWidth + extraWidth, contentHeight + extraHeight);

        if (input.PerformLayout) {
            tree.SetChildLayout(id, new LayoutResult {
                Location = Point.Zero,
                Size = size,
                ContentSize = new Size(resolvedContentWidth, contentHeight),
                Border = border,
                Padding = padding,
                Order = 0
            });
        }

        return size;
    }
}

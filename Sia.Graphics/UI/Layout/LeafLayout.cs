namespace Sia.Graphics.UI;

internal static class LeafLayout
{
    public static Size Compute(LayoutTree tree, LayoutNodeId id, ILayoutMeasure measure, LayoutInput input)
    {
        var style = tree.GetStyle(id);

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

        var contentAvailable = new AvailableSize(
            explicitWidth is { } ew ? AvailableSpace.Definite(MathF.Max(0f, ew - extraWidth)) : input.AvailableSpace.Width,
            explicitHeight is { } eh ? AvailableSpace.Definite(MathF.Max(0f, eh - extraHeight)) : input.AvailableSpace.Height);

        var measured = measure.Measure(
            new PartialSize(
                explicitWidth is { } kw ? kw - extraWidth : null,
                explicitHeight is { } kh ? kh - extraHeight : null),
            contentAvailable);

        var contentWidth = explicitWidth is { } cw ? cw - extraWidth : measured.Width;
        var contentHeight = explicitHeight is { } ch ? ch - extraHeight : measured.Height;

        var minWidth = EdgeResolution.ResolveContentConstraint(style, style.MinWidth, input, parentWidth, extraWidth);
        var maxWidth = EdgeResolution.ResolveContentConstraint(style, style.MaxWidth, input, parentWidth, extraWidth);
        var minHeight = EdgeResolution.ResolveContentConstraint(style, style.MinHeight, input, parentHeight, extraHeight);
        var maxHeight = EdgeResolution.ResolveContentConstraint(style, style.MaxHeight, input, parentHeight, extraHeight);

        contentWidth = EdgeResolution.Clamp(contentWidth, minWidth, maxWidth);
        contentHeight = EdgeResolution.Clamp(contentHeight, minHeight, maxHeight);

        var size = new Size(contentWidth + extraWidth, contentHeight + extraHeight);

        if (input.PerformLayout) {
            tree.SetChildLayout(id, new LayoutResult {
                Location = Point.Zero,
                Size = size,
                ContentSize = new Size(contentWidth, contentHeight),
                Border = border,
                Padding = padding,
                Baseline = measure.Baseline is { } baseline ? border.Top + padding.Top + baseline : null,
                Order = 0
            });
        }

        return size;
    }
}

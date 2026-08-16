namespace Sia.Graphics.UI;

internal static partial class GridLayout
{
    internal struct GridTrack
    {
        public GridTrackSizingFunction Sizing;
        public float BaseSize;
        public float GrowthLimit;
    }

    internal struct GridItem
    {
        public LayoutNodeId Node;
        public Node Style;
        public int RowStart, RowSpan;
        public int ColStart, ColSpan;
    }

    public static Size Compute(LayoutTree tree, LayoutNodeId id, LayoutInput input)
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

        float? containerContentWidth = explicitWidth is { } ew ? MathF.Max(0f, ew - extraWidth)
            : input.AvailableSpace.Width.IsDefinite ? MathF.Max(0f, input.AvailableSpace.Width.Value - extraWidth) : null;
        float? containerContentHeight = explicitHeight is { } eh ? MathF.Max(0f, eh - extraHeight)
            : input.AvailableSpace.Height.IsDefinite ? MathF.Max(0f, input.AvailableSpace.Height.Value - extraHeight) : null;

        var columnGap = input.ResolveOrZero(style.ColumnGap, containerContentWidth ?? 0f);
        var rowGap = input.ResolveOrZero(style.RowGap, containerContentHeight ?? 0f);

        var childIds = tree.GetChildren(id)
            .Where(c => tree.GetStyle(c).Display != Display.None && tree.GetStyle(c).PositionType != PositionType.Absolute)
            .ToList();

        var items = new List<GridItem>(childIds.Count);
        foreach (var c in childIds)
            items.Add(new GridItem { Node = c, Style = tree.GetStyle(c) });

        PlaceItems(style, items, out var columnCount, out var rowCount);

        var columns = BuildTracks(style.GridTemplateColumns, style.GridAutoColumns, columnCount);
        var rows = BuildTracks(style.GridTemplateRows, style.GridAutoRows, rowCount);

        SizeTracks(tree, input, items, columns, isColumns: true, columnGap, containerContentWidth);
        SizeTracks(tree, input, items, rows, isColumns: false, rowGap, containerContentHeight);

        var usedWidth = containerContentWidth ?? SumTracks(columns, columnGap);
        var usedHeight = containerContentHeight ?? SumTracks(rows, rowGap);

        var minWidth = EdgeResolution.ResolveContentConstraint(style, style.MinWidth, input, parentWidth, extraWidth);
        var maxWidth = EdgeResolution.ResolveContentConstraint(style, style.MaxWidth, input, parentWidth, extraWidth);
        var minHeight = EdgeResolution.ResolveContentConstraint(style, style.MinHeight, input, parentHeight, extraHeight);
        var maxHeight = EdgeResolution.ResolveContentConstraint(style, style.MaxHeight, input, parentHeight, extraHeight);
        var contentWidth = EdgeResolution.Clamp(usedWidth, minWidth, maxWidth);
        var contentHeight = EdgeResolution.Clamp(usedHeight, minHeight, maxHeight);

        var size = new Size(contentWidth + extraWidth, contentHeight + extraHeight);

        if (input.PerformLayout) {
            tree.SetChildLayout(id, new LayoutResult {
                Location = Point.Zero,
                Size = size,
                ContentSize = new Size(contentWidth, contentHeight),
                Border = border,
                Padding = padding,
                Order = 0
            });
            PlaceItemBoxes(tree, input, style, items, columns, rows, columnGap, rowGap);
        }

        return size;
    }

    private static float SumTracks(List<GridTrack> tracks, float gap) =>
        tracks.Count == 0 ? 0f : tracks.Sum(t => t.BaseSize) + gap * MathF.Max(0, tracks.Count - 1);

    private static List<GridTrack> BuildTracks(
        List<GridTrackSizingFunction> explicitTracks, List<GridTrackSizingFunction> autoTracks, int count)
    {
        var tracks = new List<GridTrack>(count);
        for (var i = 0; i < count; i++) {
            var sizing = i < explicitTracks.Count
                ? explicitTracks[i]
                : autoTracks.Count > 0 ? autoTracks[(i - explicitTracks.Count) % autoTracks.Count] : GridTrackSizingFunction.Auto;
            tracks.Add(new GridTrack { Sizing = sizing, BaseSize = 0f, GrowthLimit = float.PositiveInfinity });
        }
        return tracks;
    }

    private static void PlaceItemBoxes(
        LayoutTree tree, LayoutInput input, Node containerStyle, List<GridItem> items,
        List<GridTrack> columns, List<GridTrack> rows, float columnGap, float rowGap)
    {
        var colOffsets = TrackOffsets(columns, columnGap);
        var rowOffsets = TrackOffsets(rows, rowGap);

        foreach (var item in items) {
            var colStart = System.Math.Clamp(item.ColStart, 0, System.Math.Max(0, columns.Count - 1));
            var colEnd = System.Math.Clamp(item.ColStart + item.ColSpan, 1, columns.Count);
            var rowStart = System.Math.Clamp(item.RowStart, 0, System.Math.Max(0, rows.Count - 1));
            var rowEnd = System.Math.Clamp(item.RowStart + item.RowSpan, 1, rows.Count);

            var areaX = colOffsets[colStart];
            var areaWidth = (colEnd > colStart ? colOffsets[colEnd] - columnGap : colOffsets[colStart]) - areaX;
            var areaY = rowOffsets[rowStart];
            var areaHeight = (rowEnd > rowStart ? rowOffsets[rowEnd] - rowGap : rowOffsets[rowStart]) - areaY;
            areaWidth = MathF.Max(0f, areaWidth);
            areaHeight = MathF.Max(0f, areaHeight);

            var margin = EdgeResolution.Resolve(item.Style.Margin, input, areaWidth);

            var justify = FlexboxLayout.ResolveAlign(item.Style.JustifySelf, ResolveJustifyItems(containerStyle));
            var align = FlexboxLayout.ResolveAlign(item.Style.AlignSelf, containerStyle.AlignItems);

            var explicitW = input.Resolve(item.Style.Width, areaWidth);
            var explicitH = input.Resolve(item.Style.Height, areaHeight);

            var innerWidth = MathF.Max(0f, areaWidth - margin.Left - margin.Right);
            var innerHeight = MathF.Max(0f, areaHeight - margin.Top - margin.Bottom);

            var boxWidth = explicitW is { } ew ? EdgeResolution.ToBorderBox(
                item.Style, ew, BorderPaddingWidth(input, item.Style, areaWidth))
                : justify == AlignItems.Stretch ? innerWidth : (float?)null;
            var boxHeight = explicitH is { } eh ? EdgeResolution.ToBorderBox(
                item.Style, eh, BorderPaddingHeight(input, item.Style, areaWidth))
                : align == AlignItems.Stretch ? innerHeight : (float?)null;

            var childInput = input with {
                KnownDimensions = new PartialSize(boxWidth, boxHeight),
                ParentSize = new PartialSize(areaWidth, areaHeight),
                AvailableSpace = new AvailableSize(
                    boxWidth is { } bw ? AvailableSpace.Definite(bw) : AvailableSpace.Definite(innerWidth),
                    boxHeight is { } bh ? AvailableSpace.Definite(bh) : AvailableSpace.Definite(innerHeight)),
                PerformLayout = true
            };
            var size = tree.ComputeNodeSize(item.Node, childInput);

            var freeX = MathF.Max(0f, innerWidth - size.Width);
            var freeY = MathF.Max(0f, innerHeight - size.Height);
            var offsetX = justify switch { AlignItems.End or AlignItems.FlexEnd => freeX, AlignItems.Center => freeX / 2f, _ => 0f };
            var offsetY = align switch { AlignItems.End or AlignItems.FlexEnd => freeY, AlignItems.Center => freeY / 2f, _ => 0f };

            var layout = tree.GetLayout(item.Node);
            layout.Location = new Point(areaX + margin.Left + offsetX, areaY + margin.Top + offsetY);
            tree.SetChildLayout(item.Node, layout);
        }
    }

    private static AlignItems ResolveJustifyItems(Node containerStyle) =>
        containerStyle.JustifyItems == AlignItems.Default ? AlignItems.Stretch : containerStyle.JustifyItems;

    private static float BorderPaddingWidth(LayoutInput input, Node style, float basis)
    {
        var b = EdgeResolution.Resolve(style.Border, input, basis);
        var p = EdgeResolution.Resolve(style.Padding, input, basis);
        return b.Left + b.Right + p.Left + p.Right;
    }

    private static float BorderPaddingHeight(LayoutInput input, Node style, float basis)
    {
        var b = EdgeResolution.Resolve(style.Border, input, basis);
        var p = EdgeResolution.Resolve(style.Padding, input, basis);
        return b.Top + b.Bottom + p.Top + p.Bottom;
    }

    private static float[] TrackOffsets(List<GridTrack> tracks, float gap)
    {
        var offsets = new float[tracks.Count + 1];
        var cursor = 0f;
        for (var i = 0; i < tracks.Count; i++) {
            offsets[i] = cursor;
            cursor += tracks[i].BaseSize + gap;
        }
        offsets[tracks.Count] = cursor;
        return offsets;
    }
}

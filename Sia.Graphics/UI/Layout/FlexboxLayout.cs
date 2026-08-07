namespace Sia.Graphics.UI;

internal static partial class FlexboxLayout
{
    internal struct FlexItem
    {
        public LayoutNodeId Node;
        public Node Style;
        public BorderEdges Margin;
        public BorderEdges Border;
        public BorderEdges Padding;
        public float MarginMainSum;
        public float MarginCrossSum;
        public bool MarginMainAutoStart;
        public bool MarginMainAutoEnd;
        public bool MarginCrossAutoStart;
        public bool MarginCrossAutoEnd;

        public float FlexBasis;
        public float HypotheticalMainSize;
        public float TargetMainSize;

        public float HypotheticalCrossSize;
        public float TargetCrossSize;
        public float? Baseline;

        public float MinMain, MaxMain;
        public float MinCross, MaxCross;

        public Point Location;
    }

    internal struct FlexLine
    {
        public List<int> Items;
        public float CrossSize;
        public float UsedMainSpace;
        public float CrossOffset;
        public float Baseline;
    }

    internal sealed class FlexContext(
        LayoutTree tree,
        Node containerStyle,
        LayoutInput input,
        UiAxis mainAxis,
        BorderEdges border,
        BorderEdges padding,
        float containerMainSize,
        float? containerCrossSize,
        float mainGap,
        float crossGap)
    {
        public LayoutTree Tree = tree;
        public Node ContainerStyle = containerStyle;
        public LayoutInput Input = input;
        public UiAxis MainAxis = mainAxis;
        public BorderEdges Border = border;
        public BorderEdges Padding = padding;
        public float ContainerMainSize = containerMainSize;
        public float? ContainerCrossSize = containerCrossSize;
        public float MainGap = mainGap;
        public float CrossGap = crossGap;
        public List<FlexLine> Lines = [];
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

        var mainAxis = style.FlexDirection.MainAxis();

        var containerContentWidth = explicitWidth is { } ew
            ? MathF.Max(0f, ew - extraWidth)
            : input.AvailableSpace.Width.IsDefinite ? MathF.Max(0f, input.AvailableSpace.Width.Value - extraWidth) : 0f;
        var widthIsDefinite = explicitWidth is { } || input.AvailableSpace.Width.IsDefinite;

        var containerContentHeight = explicitHeight is { } eh
            ? MathF.Max(0f, eh - extraHeight)
            : input.AvailableSpace.Height.IsDefinite ? MathF.Max(0f, input.AvailableSpace.Height.Value - extraHeight) : 0f;
        var heightIsDefinite = explicitHeight is { } || input.AvailableSpace.Height.IsDefinite;

        var mainSize = mainAxis == UiAxis.Horizontal ? containerContentWidth : containerContentHeight;
        var mainDefinite = mainAxis == UiAxis.Horizontal ? widthIsDefinite : heightIsDefinite;
        var crossDefinite = mainAxis == UiAxis.Horizontal ? heightIsDefinite : widthIsDefinite;
        var crossSize = mainAxis == UiAxis.Horizontal ? containerContentHeight : containerContentWidth;

        var mainGap = input.ResolveOrZero(mainAxis == UiAxis.Horizontal ? style.ColumnGap : style.RowGap, mainSize);
        var crossGap = input.ResolveOrZero(mainAxis == UiAxis.Horizontal ? style.RowGap : style.ColumnGap, crossSize);

        var ctx = new FlexContext(
            tree, style, input, mainAxis, border, padding,
            mainSize, crossDefinite ? crossSize : null, mainGap, crossGap);

        var childIds = tree.GetChildren(id)
            .Where(c => tree.GetStyle(c).Display != Display.None && tree.GetStyle(c).PositionType != PositionType.Absolute)
            .ToList();

        var items = new List<FlexItem>(childIds.Count);
        foreach (var child in childIds)
            items.Add(CreateFlexItem(ctx, child, mainDefinite ? mainSize : null));

        BuildLines(ctx, items, mainDefinite ? mainSize : null);
        ResolveFlexibleLengths(ctx, items, mainDefinite ? mainSize : null);
        DetermineCrossSizes(ctx, items);

        var usedCrossSize = ctx.ContainerCrossSize
            ?? ctx.Lines.Sum(l => l.CrossSize) + MathF.Max(0, ctx.Lines.Count - 1) * crossGap;
        AlignAndPlace(ctx, items, mainDefinite ? mainSize : null, usedCrossSize);

        var contentMain = mainDefinite
            ? mainSize
            : ctx.Lines.Count == 0 ? 0f : ctx.Lines.Max(l => l.UsedMainSpace);
        var contentCross = usedCrossSize;

        var (contentWidth, contentHeight) = mainAxis == UiAxis.Horizontal
            ? (contentMain, contentCross)
            : (contentCross, contentMain);

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
                Order = 0
            });
            WriteBackPlacement(ctx, items);
        }

        return size;
    }

    private static FlexItem CreateFlexItem(FlexContext ctx, LayoutNodeId node, float? definiteMainSize)
    {
        var style = ctx.Tree.GetStyle(node);
        var input = ctx.Input;
        var containerMainBasis = definiteMainSize ?? ctx.ContainerMainSize;
        var containerCrossBasis = ctx.ContainerCrossSize
            ?? (ctx.MainAxis == UiAxis.Horizontal
                ? input.ParentSize.Height
                : input.ParentSize.Width)
            ?? 0f;

        var margin = EdgeResolution.Resolve(style.Margin, input, containerMainBasis);
        var border = EdgeResolution.Resolve(style.Border, input, containerMainBasis);
        var padding = EdgeResolution.Resolve(style.Padding, input, containerMainBasis);

        var horizontal = ctx.MainAxis == UiAxis.Horizontal;
        var (mainSizeVal, crossSizeVal) = horizontal ? (style.Width, style.Height) : (style.Height, style.Width);
        var (mainMinVal, crossMinVal) = horizontal ? (style.MinWidth, style.MinHeight) : (style.MinHeight, style.MinWidth);
        var (mainMaxVal, crossMaxVal) = horizontal ? (style.MaxWidth, style.MaxHeight) : (style.MaxHeight, style.MaxWidth);

        var mainBorderPadding = horizontal
            ? border.Left + border.Right + padding.Left + padding.Right
            : border.Top + border.Bottom + padding.Top + padding.Bottom;
        var crossBorderPadding = horizontal
            ? border.Top + border.Bottom + padding.Top + padding.Bottom
            : border.Left + border.Right + padding.Left + padding.Right;

        var marginMainAutoStart = horizontal ? style.Margin.Left.IsAuto : style.Margin.Top.IsAuto;
        var marginMainAutoEnd = horizontal ? style.Margin.Right.IsAuto : style.Margin.Bottom.IsAuto;
        var marginCrossAutoStart = horizontal ? style.Margin.Top.IsAuto : style.Margin.Left.IsAuto;
        var marginCrossAutoEnd = horizontal ? style.Margin.Bottom.IsAuto : style.Margin.Right.IsAuto;

        var marginMainSum = (marginMainAutoStart ? 0f : horizontal ? margin.Left : margin.Top)
            + (marginMainAutoEnd ? 0f : horizontal ? margin.Right : margin.Bottom);
        var marginCrossSum = (marginCrossAutoStart ? 0f : horizontal ? margin.Top : margin.Left)
            + (marginCrossAutoEnd ? 0f : horizontal ? margin.Bottom : margin.Right);

        var minMain = input.Resolve(mainMinVal, containerMainBasis) is { } mn
            ? EdgeResolution.ToBorderBox(style, mn, mainBorderPadding) : 0f;
        var maxMain = input.Resolve(mainMaxVal, containerMainBasis) is { } mx
            ? EdgeResolution.ToBorderBox(style, mx, mainBorderPadding) : float.PositiveInfinity;
        var minCross = input.Resolve(crossMinVal, containerCrossBasis) is { } mnc
            ? EdgeResolution.ToBorderBox(style, mnc, crossBorderPadding) : 0f;
        var maxCross = input.Resolve(crossMaxVal, containerCrossBasis) is { } mxc
            ? EdgeResolution.ToBorderBox(style, mxc, crossBorderPadding) : float.PositiveInfinity;

        var basis = ResolveFlexBasis(ctx, node, style, mainSizeVal, mainBorderPadding, containerMainBasis);
        var hypotheticalMain = EdgeResolution.Clamp(basis, minMain, maxMain);

        var explicitCross = input.Resolve(crossSizeVal, containerCrossBasis);
        var hypotheticalCross = explicitCross is { } ec
            ? EdgeResolution.Clamp(EdgeResolution.ToBorderBox(style, ec, crossBorderPadding), minCross, maxCross) : 0f;

        return new FlexItem {
            Node = node,
            Style = style,
            Margin = margin,
            Border = border,
            Padding = padding,
            MarginMainSum = marginMainSum,
            MarginCrossSum = marginCrossSum,
            MarginMainAutoStart = marginMainAutoStart,
            MarginMainAutoEnd = marginMainAutoEnd,
            MarginCrossAutoStart = marginCrossAutoStart,
            MarginCrossAutoEnd = marginCrossAutoEnd,
            FlexBasis = basis,
            HypotheticalMainSize = hypotheticalMain,
            TargetMainSize = hypotheticalMain,
            MinMain = minMain,
            MaxMain = maxMain,
            MinCross = minCross,
            MaxCross = maxCross,
            HypotheticalCrossSize = hypotheticalCross,
            TargetCrossSize = hypotheticalCross
        };
    }

    private static float ResolveFlexBasis(
        FlexContext ctx, LayoutNodeId node, Node style, Val axisSizeVal, float mainBorderPadding, float containerMainBasis)
    {
        var input = ctx.Input;
        if (!style.FlexBasis.IsAuto)
            return EdgeResolution.ToBorderBox(
                style, input.Resolve(style.FlexBasis, containerMainBasis) ?? 0f, mainBorderPadding);

        if (input.Resolve(axisSizeVal, containerMainBasis) is { } explicitSize)
            return EdgeResolution.ToBorderBox(style, explicitSize, mainBorderPadding);

        var probeInput = input with {
            KnownDimensions = PartialSize.Unknown,
            ParentSize = new PartialSize(containerMainBasis, null),
            AvailableSpace = new AvailableSize(AvailableSpace.MaxContent, AvailableSpace.MaxContent),
            PerformLayout = false
        };
        var size = ctx.Tree.ComputeNodeSize(node, probeInput);
        return ctx.MainAxis == UiAxis.Horizontal ? size.Width : size.Height;
    }
}

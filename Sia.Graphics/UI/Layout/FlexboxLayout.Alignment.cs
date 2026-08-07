using System.Runtime.InteropServices;

namespace Sia.Graphics.UI;

internal static partial class FlexboxLayout
{
    private static (float Leading, float Between) DistributeSpace(AlignContent mode, float free, int count)
    {
        if (count <= 0) return (0f, 0f);
        var f = MathF.Max(free, 0f);
        return mode switch {
            AlignContent.End or AlignContent.FlexEnd => (f, 0f),
            AlignContent.Center => (f / 2f, 0f),
            AlignContent.SpaceBetween => count > 1 ? (0f, f / (count - 1)) : (f / 2f, 0f),
            AlignContent.SpaceAround => (f / count / 2f, f / count),
            AlignContent.SpaceEvenly => (f / (count + 1), f / (count + 1)),
            _ => (0f, 0f)
        };
    }

    private static void AlignAndPlace(FlexContext ctx, List<FlexItem> items, float? mainSize, float usedCrossSize)
    {
        var span = CollectionsMarshal.AsSpan(items);
        var reverse = ctx.ContainerStyle.FlexDirection.IsReversed();
        var wrapReverse = ctx.ContainerStyle.FlexWrap == FlexWrap.WrapReverse;

        var totalLinesCross = ctx.Lines.Sum(l => l.CrossSize) + ctx.CrossGap * MathF.Max(0, ctx.Lines.Count - 1);
        var freeCross = usedCrossSize - totalLinesCross;
        var contentAlign = ctx.ContainerStyle.AlignContent == AlignContent.Default
            ? AlignContent.Stretch
            : ctx.ContainerStyle.AlignContent;

        if (contentAlign == AlignContent.Stretch && ctx.Lines.Count > 0 && freeCross > 0f) {
            var extraPerLine = freeCross / ctx.Lines.Count;
            for (var li = 0; li < ctx.Lines.Count; li++) {
                var line = ctx.Lines[li];
                line.CrossSize += extraPerLine;
                ctx.Lines[li] = line;
            }
            freeCross = 0f;
        }

        var (crossLeading, crossBetween) = DistributeSpace(contentAlign, freeCross, ctx.Lines.Count);

        var lineOrder = Enumerable.Range(0, ctx.Lines.Count).ToList();
        if (wrapReverse) lineOrder.Reverse();

        var crossCursor = crossLeading;
        foreach (var li in lineOrder) {
            var line = ctx.Lines[li];
            line.CrossOffset = crossCursor;
            ctx.Lines[li] = line;
            crossCursor += line.CrossSize + ctx.CrossGap + crossBetween;

            PlaceLine(ctx, span, ctx.Lines[li], reverse, mainSize);
        }
    }

    private static void PlaceLine(FlexContext ctx, Span<FlexItem> span, FlexLine line, bool reverse, float? mainSize)
    {
        var indices = line.Items;
        if (indices.Count == 0) return;

        var visual = reverse ? Enumerable.Reverse(indices).ToList() : indices;

        var autoMarginSlots = 0;
        foreach (var i in indices) {
            if (span[i].MarginMainAutoStart) autoMarginSlots++;
            if (span[i].MarginMainAutoEnd) autoMarginSlots++;
        }

        var limit = mainSize ?? line.UsedMainSpace;
        var freeMain = MathF.Max(0f, limit - line.UsedMainSpace);

        float leading = 0f, between = 0f, autoShare = 0f;
        if (autoMarginSlots > 0 && freeMain > 0f) {
            autoShare = freeMain / autoMarginSlots;
        } else {
            (leading, between) = DistributeSpace(ctx.ContainerStyle.JustifyContent, freeMain, indices.Count);
        }

        var cursor = leading;
        foreach (var i in visual) {
            ref var item = ref span[i];
            var horizontal = ctx.MainAxis == UiAxis.Horizontal;

            var marginStart = item.MarginMainAutoStart ? autoShare : horizontal ? item.Margin.Left : item.Margin.Top;
            var marginEnd = item.MarginMainAutoEnd ? autoShare : horizontal ? item.Margin.Right : item.Margin.Bottom;

            cursor += marginStart;
            var mainPos = cursor;
            cursor += item.TargetMainSize + marginEnd + ctx.MainGap + between;

            var align = ResolveAlign(item.Style.AlignSelf, ctx.ContainerStyle.AlignItems);
            var crossAutoSlots = (item.MarginCrossAutoStart ? 1 : 0) + (item.MarginCrossAutoEnd ? 1 : 0);
            var freeInLineCross = MathF.Max(0f, line.CrossSize - (item.TargetCrossSize + item.MarginCrossSum));

            float crossPos;
            var marginCrossStart = horizontal ? item.Margin.Top : item.Margin.Left;
            if (crossAutoSlots > 0 && freeInLineCross > 0f) {
                var share = freeInLineCross / crossAutoSlots;
                crossPos = line.CrossOffset + (item.MarginCrossAutoStart ? share : marginCrossStart);
            } else {
                var offset = align switch {
                    AlignItems.End or AlignItems.FlexEnd => freeInLineCross,
                    AlignItems.Center => freeInLineCross / 2f,
                    AlignItems.Baseline when horizontal =>
                        line.Baseline - (item.Baseline ?? item.TargetCrossSize),
                    _ => 0f
                };
                crossPos = line.CrossOffset + offset + marginCrossStart;
            }

            item.Location = horizontal ? new Point(mainPos, crossPos) : new Point(crossPos, mainPos);
        }
    }

    private static void WriteBackPlacement(FlexContext ctx, List<FlexItem> items)
    {
        var span = CollectionsMarshal.AsSpan(items);
        for (var i = 0; i < items.Count; i++) {
            var item = span[i];
            var horizontal = ctx.MainAxis == UiAxis.Horizontal;
            var width = horizontal ? item.TargetMainSize : item.TargetCrossSize;
            var height = horizontal ? item.TargetCrossSize : item.TargetMainSize;

            var childInput = ctx.Input with {
                KnownDimensions = new PartialSize(width, height),
                ParentSize = new PartialSize(width, height),
                AvailableSpace = new AvailableSize(AvailableSpace.Definite(width), AvailableSpace.Definite(height)),
                PerformLayout = true
            };
            ctx.Tree.ComputeNodeSize(item.Node, childInput);

            var layout = ctx.Tree.GetLayout(item.Node);
            layout.Location = item.Location;
            ctx.Tree.SetChildLayout(item.Node, layout);
        }
    }
}

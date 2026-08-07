using System.Runtime.InteropServices;

namespace Sia.Graphics.UI;

internal static partial class FlexboxLayout
{
    private static void BuildLines(FlexContext ctx, List<FlexItem> items, float? mainSize)
    {
        ctx.Lines.Clear();
        if (ctx.ContainerStyle.FlexWrap == FlexWrap.NoWrap || mainSize is not { } limit) {
            ctx.Lines.Add(new FlexLine { Items = Enumerable.Range(0, items.Count).ToList() });
            return;
        }

        var current = new List<int>();
        var used = 0f;
        for (var i = 0; i < items.Count; i++) {
            var outer = items[i].HypotheticalMainSize + items[i].MarginMainSum;
            var withGap = current.Count == 0 ? outer : used + ctx.MainGap + outer;
            if (current.Count > 0 && withGap > limit + 0.001f) {
                ctx.Lines.Add(new FlexLine { Items = current });
                current = [i];
                used = outer;
            } else {
                current.Add(i);
                used = withGap;
            }
        }
        if (current.Count > 0)
            ctx.Lines.Add(new FlexLine { Items = current });
    }

    private static void ResolveFlexibleLengths(FlexContext ctx, List<FlexItem> items, float? mainSize)
    {
        var span = CollectionsMarshal.AsSpan(items);

        for (var li = 0; li < ctx.Lines.Count; li++) {
            var line = ctx.Lines[li];
            var indices = line.Items;
            if (indices.Count == 0) continue;

            var basisSum = SumMain(span, indices, useTarget: false) + ctx.MainGap * (indices.Count - 1);

            if (mainSize is not { } limit) {
                foreach (var i in indices)
                    span[i].TargetMainSize = span[i].HypotheticalMainSize;
                line.UsedMainSpace = basisSum;
                ctx.Lines[li] = line;
                continue;
            }

            var growing = limit >= basisSum;
            var frozen = new bool[indices.Count];

            for (var k = 0; k < indices.Count; k++) {
                var participates = growing ? span[indices[k]].Style.FlexGrow > 0f : span[indices[k]].Style.FlexShrink > 0f;
                if (!participates) {
                    span[indices[k]].TargetMainSize = span[indices[k]].HypotheticalMainSize;
                    frozen[k] = true;
                }
            }

            for (var iter = 0; iter < indices.Count + 1; iter++) {
                if (frozen.All(f => f)) break;

                var usedByFrozen = 0f;
                var basisUnfrozen = 0f;
                var growSum = 0f;
                var shrinkSum = 0f;
                for (var k = 0; k < indices.Count; k++) {
                    var item = span[indices[k]];
                    if (frozen[k]) {
                        usedByFrozen += item.TargetMainSize + item.MarginMainSum;
                    } else {
                        basisUnfrozen += item.HypotheticalMainSize + item.MarginMainSum;
                        growSum += item.Style.FlexGrow;
                        shrinkSum += item.Style.FlexShrink * item.HypotheticalMainSize;
                    }
                }
                var gapTotal = ctx.MainGap * (indices.Count - 1);
                var remaining = limit - usedByFrozen - basisUnfrozen - gapTotal;

                if (MathF.Abs(remaining) < 0.001f || (growing && growSum <= 0f) || (!growing && shrinkSum <= 0f)) {
                    for (var k = 0; k < indices.Count; k++) {
                        if (frozen[k]) continue;
                        span[indices[k]].TargetMainSize = span[indices[k]].HypotheticalMainSize;
                    }
                    break;
                }

                var anyClamped = false;
                for (var k = 0; k < indices.Count; k++) {
                    if (frozen[k]) continue;
                    var item = span[indices[k]];
                    var share = growing
                        ? remaining * (item.Style.FlexGrow / growSum)
                        : remaining * ((item.Style.FlexShrink * item.HypotheticalMainSize) / shrinkSum);
                    var target = item.HypotheticalMainSize + share;
                    var clamped = EdgeResolution.Clamp(target, item.MinMain, item.MaxMain);
                    span[indices[k]].TargetMainSize = clamped;
                    if (MathF.Abs(clamped - target) > 0.001f) {
                        frozen[k] = true;
                        anyClamped = true;
                    }
                }
                if (!anyClamped) break;
            }

            line.UsedMainSpace = SumMain(span, indices, useTarget: true) + ctx.MainGap * (indices.Count - 1);
            ctx.Lines[li] = line;
        }
    }

    private static float SumMain(Span<FlexItem> span, List<int> indices, bool useTarget)
    {
        var sum = 0f;
        foreach (var i in indices)
            sum += (useTarget ? span[i].TargetMainSize : span[i].HypotheticalMainSize) + span[i].MarginMainSum;
        return sum;
    }

    private static void DetermineCrossSizes(FlexContext ctx, List<FlexItem> items)
    {
        var span = CollectionsMarshal.AsSpan(items);

        for (var i = 0; i < items.Count; i++) {
            var hasExplicitCross = HasExplicitCross(ctx, span[i].Style);
            var needsBaseline = ctx.MainAxis == UiAxis.Horizontal
                && ResolveAlign(span[i].Style.AlignSelf, ctx.ContainerStyle.AlignItems) == AlignItems.Baseline;
            if (hasExplicitCross && !needsBaseline)
                continue;

            var borderBoxMain = span[i].TargetMainSize;
            var known = ctx.MainAxis == UiAxis.Horizontal
                ? new PartialSize(borderBoxMain, null)
                : new PartialSize(null, borderBoxMain);
            var probe = ctx.Input with {
                KnownDimensions = known,
                ParentSize = new PartialSize(
                    ctx.MainAxis == UiAxis.Horizontal ? borderBoxMain : ctx.ContainerCrossSize,
                    ctx.MainAxis == UiAxis.Horizontal ? ctx.ContainerCrossSize : borderBoxMain),
                AvailableSpace = new AvailableSize(AvailableSpace.MaxContent, AvailableSpace.MaxContent),
                PerformLayout = false
            };
            var size = ctx.Tree.ComputeNodeSize(span[i].Node, probe);
            span[i].Baseline = ctx.Tree.GetBaseline(span[i].Node);
            // `size` from `ComputeNodeSize` is already a border-box size.
            if (!hasExplicitCross) {
                var content = ctx.MainAxis == UiAxis.Horizontal ? size.Height : size.Width;
                span[i].HypotheticalCrossSize = EdgeResolution.Clamp(content, span[i].MinCross, span[i].MaxCross);
            }
        }

        for (var li = 0; li < ctx.Lines.Count; li++) {
            var line = ctx.Lines[li];
            var lineCross = ctx.Lines.Count == 1 && ctx.ContainerCrossSize is { } fixedCross
                ? fixedCross
                : MaxCross(span, line.Items);
            line.CrossSize = lineCross;
            ctx.Lines[li] = line;

            foreach (var i in line.Items) {
                var stretches = !HasExplicitCross(ctx, span[i].Style)
                    && ResolveAlign(span[i].Style.AlignSelf, ctx.ContainerStyle.AlignItems) == AlignItems.Stretch;
                span[i].TargetCrossSize = stretches
                    ? EdgeResolution.Clamp(lineCross - span[i].MarginCrossSum, span[i].MinCross, span[i].MaxCross)
                    : span[i].HypotheticalCrossSize;
            }

            if (ctx.MainAxis == UiAxis.Horizontal) {
                var baselineCount = 0;
                var maxBaseline = 0f;
                var maxBelowBaseline = 0f;
                foreach (var itemIndex in line.Items) {
                    ref var item = ref span[itemIndex];
                    if (ResolveAlign(item.Style.AlignSelf, ctx.ContainerStyle.AlignItems) != AlignItems.Baseline)
                        continue;
                    baselineCount++;
                    var baseline = item.Baseline ?? item.TargetCrossSize;
                    maxBaseline = MathF.Max(maxBaseline, baseline);
                    maxBelowBaseline = MathF.Max(maxBelowBaseline, item.TargetCrossSize - baseline);
                }
                if (baselineCount > 0) {
                    line.Baseline = maxBaseline;
                    line.CrossSize = MathF.Max(line.CrossSize, line.Baseline + maxBelowBaseline);
                    ctx.Lines[li] = line;
                }
            }
        }
    }

    private static float MaxCross(Span<FlexItem> span, List<int> indices)
    {
        if (indices.Count == 0) return 0f;
        var max = 0f;
        foreach (var i in indices)
            max = MathF.Max(max, span[i].HypotheticalCrossSize + span[i].MarginCrossSum);
        return max;
    }

    private static bool HasExplicitCross(FlexContext ctx, Node style) =>
        !(ctx.MainAxis == UiAxis.Horizontal ? style.Height : style.Width).IsAuto;

    private static float MainBorderPadding(FlexContext ctx, in FlexItem item) => ctx.MainAxis == UiAxis.Horizontal
        ? item.Border.Left + item.Border.Right + item.Padding.Left + item.Padding.Right
        : item.Border.Top + item.Border.Bottom + item.Padding.Top + item.Padding.Bottom;

    internal static AlignItems ResolveAlign(AlignItems self, AlignItems containerItems)
    {
        if (self != AlignItems.Default)
            return self;
        return containerItems == AlignItems.Default ? AlignItems.Stretch : containerItems;
    }
}

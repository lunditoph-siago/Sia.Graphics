using System.Runtime.InteropServices;

namespace Sia.Graphics.UI;

internal static partial class GridLayout
{
    private static void SizeTracks(
        LayoutTree tree, LayoutInput input, List<GridItem> items, List<GridTrack> tracks,
        bool isColumns, float gap, float? availableSize)
    {
        if (tracks.Count == 0) return;
        var trackSpan = CollectionsMarshal.AsSpan(tracks);
        var basis = availableSize ?? 0f;
        var fitContentCap = new float?[trackSpan.Length];

        for (var i = 0; i < trackSpan.Length; i++) {
            var sizing = trackSpan[i].Sizing;
            trackSpan[i].BaseSize = sizing.Min.Kind == MinTrackSizingFunctionKind.Fixed
                ? input.ResolveOrZero(sizing.Min.Value, basis)
                : 0f;
            trackSpan[i].GrowthLimit = sizing.Max.Kind switch {
                MaxTrackSizingFunctionKind.Fixed => input.ResolveOrZero(sizing.Max.Value, basis),
                MaxTrackSizingFunctionKind.Fraction or MaxTrackSizingFunctionKind.Auto => float.PositiveInfinity,
                _ => 0f
            };
            if (sizing.Max.Kind == MaxTrackSizingFunctionKind.FitContent)
                fitContentCap[i] = input.ResolveOrZero(sizing.Max.Value, basis);
        }

        ResolveIntrinsicSizes(tree, input, items, trackSpan, isColumns, gap);

        for (var i = 0; i < trackSpan.Length; i++) {
            if (fitContentCap[i] is { } cap)
                trackSpan[i].GrowthLimit = MathF.Min(trackSpan[i].GrowthLimit, cap);
            if (trackSpan[i].GrowthLimit < trackSpan[i].BaseSize)
                trackSpan[i].GrowthLimit = trackSpan[i].BaseSize;
        }

        if (availableSize is { } definite) {
            MaximizeTracks(trackSpan, definite, gap);
            ExpandFlexibleTracks(trackSpan, definite, gap, basis, input);
        }
    }

    private static void ResolveIntrinsicSizes(
        LayoutTree tree, LayoutInput input, List<GridItem> items, Span<GridTrack> tracks, bool isColumns, float gap)
    {
        var needsContent = false;
        foreach (var t in tracks) {
            if (t.Sizing.Min.Kind != MinTrackSizingFunctionKind.Fixed
                || t.Sizing.Max.Kind is MaxTrackSizingFunctionKind.MinContent or MaxTrackSizingFunctionKind.MaxContent
                    or MaxTrackSizingFunctionKind.FitContent) {
                needsContent = true;
                break;
            }
        }
        if (!needsContent) return;

        foreach (var item in items) {
            var (start, span) = isColumns ? (item.ColStart, item.ColSpan) : (item.RowStart, item.RowSpan);
            if (start < 0 || start >= tracks.Length) continue;
            span = System.Math.Min(span, tracks.Length - start);
            if (span <= 0) continue;

            var track0 = tracks[start].Sizing;
            var trackIsContentBased = track0.Min.Kind != MinTrackSizingFunctionKind.Fixed
                || track0.Max.Kind is MaxTrackSizingFunctionKind.MinContent or MaxTrackSizingFunctionKind.MaxContent
                    or MaxTrackSizingFunctionKind.FitContent;
            if (span == 1 && !trackIsContentBased) continue;

            var minMeasured = MeasureItem(tree, input, item.Node, isColumns, AvailableSpace.MinContent);
            var maxMeasured = MeasureItem(tree, input, item.Node, isColumns, AvailableSpace.MaxContent);

            if (span == 1) {
                ref var track = ref tracks[start];
                if (track.Sizing.Min.Kind != MinTrackSizingFunctionKind.Fixed)
                    track.BaseSize = MathF.Max(track.BaseSize, minMeasured);
                track.GrowthLimit = track.Sizing.Max.Kind switch {
                    MaxTrackSizingFunctionKind.MinContent => MathF.Max(track.GrowthLimit, minMeasured),
                    MaxTrackSizingFunctionKind.MaxContent or MaxTrackSizingFunctionKind.FitContent
                        => MathF.Max(track.GrowthLimit, maxMeasured),
                    _ => track.GrowthLimit
                };
            }
            else {
                var existing = 0f;
                var eligibleCount = 0;
                for (var i = start; i < start + span; i++) {
                    existing += tracks[i].BaseSize;
                    if (tracks[i].Sizing.Min.Kind != MinTrackSizingFunctionKind.Fixed)
                        eligibleCount++;
                }
                existing += gap * (span - 1);
                var extra = maxMeasured - existing;
                if (extra > 0f && eligibleCount > 0) {
                    var share = extra / eligibleCount;
                    for (var i = start; i < start + span; i++) {
                        if (tracks[i].Sizing.Min.Kind == MinTrackSizingFunctionKind.Fixed) continue;
                        tracks[i].BaseSize += share;
                        tracks[i].GrowthLimit = MathF.Max(tracks[i].GrowthLimit, tracks[i].BaseSize);
                    }
                }
            }
        }
    }

    private static float MeasureItem(LayoutTree tree, LayoutInput input, LayoutNodeId node, bool isColumns, AvailableSpace mode)
    {
        var probe = input with {
            KnownDimensions = PartialSize.Unknown,
            ParentSize = PartialSize.Unknown,
            AvailableSpace = isColumns
                ? new AvailableSize(mode, AvailableSpace.MaxContent)
                : new AvailableSize(AvailableSpace.MaxContent, mode),
            PerformLayout = false
        };
        var size = tree.ComputeNodeSize(node, probe);
        return isColumns ? size.Width : size.Height;
    }

    private static void MaximizeTracks(Span<GridTrack> tracks, float availableSize, float gap)
    {
        var used = gap * MathF.Max(0, tracks.Length - 1);
        foreach (var t in tracks) used += t.BaseSize;
        var free = availableSize - used;
        if (free <= 0f) return;

        var eligible = new List<int>();
        for (var i = 0; i < tracks.Length; i++)
            if (tracks[i].Sizing.Max.Kind != MaxTrackSizingFunctionKind.Fraction)
                eligible.Add(i);

        for (var iter = 0; iter < eligible.Count + 1 && eligible.Count > 0 && free > 0.001f; iter++) {
            var share = free / eligible.Count;
            var stillEligible = new List<int>();
            foreach (var idx in eligible) {
                var capacity = tracks[idx].GrowthLimit - tracks[idx].BaseSize;
                if (capacity <= share) {
                    free -= capacity;
                    tracks[idx].BaseSize = tracks[idx].GrowthLimit;
                }
                else {
                    tracks[idx].BaseSize += share;
                    free -= share;
                    stillEligible.Add(idx);
                }
            }
            if (stillEligible.Count == eligible.Count) break;
            eligible = stillEligible;
        }
    }

    private static void ExpandFlexibleTracks(Span<GridTrack> tracks, float availableSize, float gap, float basis, LayoutInput input)
    {
        var frSum = 0f;
        var nonFrUsed = gap * MathF.Max(0, tracks.Length - 1);
        foreach (var t in tracks) {
            if (t.Sizing.Max.Kind == MaxTrackSizingFunctionKind.Fraction)
                frSum += t.Sizing.Max.Fraction;
            else
                nonFrUsed += t.BaseSize;
        }
        if (frSum <= 0f) return;

        var remaining = availableSize - nonFrUsed;
        if (remaining <= 0f) return;

        var flexFraction = remaining / frSum;
        for (var i = 0; i < tracks.Length; i++) {
            if (tracks[i].Sizing.Max.Kind != MaxTrackSizingFunctionKind.Fraction) continue;
            var size = tracks[i].Sizing.Max.Fraction * flexFraction;
            tracks[i].BaseSize = MathF.Max(tracks[i].BaseSize, size);
        }
    }
}

using Sia;
using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

internal static class UiExtraction
{
    internal static void Extract(
        IEntityQuery backgrounds,
        IEntityQuery borders,
        IEntityQuery text,
        List<ExtractedUiNode> result)
    {
        result.Clear();
        backgrounds.ForEach(
            result,
            static (in List<ExtractedUiNode> output, Entity entity) =>
                AppendBackground(entity, output));
        borders.ForEach(
            result,
            static (in List<ExtractedUiNode> output, Entity entity) =>
                AppendBorder(entity, output));
        text.ForEach(
            result,
            static (in List<ExtractedUiNode> output, Entity entity) => AppendGlyphs(entity, output));
        result.Sort(static (left, right) => {
            var stackOrder = left.StackIndex.CompareTo(right.StackIndex);
            return stackOrder != 0 ? stackOrder : left.SubOrder.CompareTo(right.SubOrder);
        });
    }

    internal static void Extract(Entity entity, List<ExtractedUiNode> result)
    {
        result.Clear();
        if (!entity.IsValid)
            return;
        if (entity.Contains<ComputedNode>() && entity.Contains<UiGlobalTransform>()) {
            if (entity.Contains<BackgroundColor>())
                AppendBackground(entity, result);
            if (entity.Contains<BorderColor>())
                AppendBorder(entity, result);
            if (entity.Contains<TextLayoutInfo>() && entity.Contains<TextStyle>())
                AppendGlyphs(entity, result);
        }
    }

    private static void AppendBackground(Entity entity, List<ExtractedUiNode> result)
    {
        var computed = entity.Get<ComputedNode>();
        result.Add(ExtractedUiNode.SolidColor(
            entity,
            Point.Zero,
            computed.Size,
            entity.Get<BackgroundColor>().Value,
            computed.BorderRadius,
            BorderEdges.Zero,
            computed.StackIndex) with {
                ClipRect = computed.ClipRect,
                Transform = entity.Get<UiGlobalTransform>(),
                SubOrder = 0
            });
    }

    private static void AppendBorder(Entity entity, List<ExtractedUiNode> result)
    {
        var computed = entity.Get<ComputedNode>();
        var border = computed.Border;
        if (border.Left <= 0f && border.Top <= 0f && border.Right <= 0f && border.Bottom <= 0f)
            return;
        result.Add(ExtractedUiNode.SolidColor(
            entity,
            Point.Zero,
            computed.Size,
            entity.Get<BorderColor>().Value,
            computed.BorderRadius,
            border,
            computed.StackIndex) with {
                ClipRect = computed.ClipRect,
                Transform = entity.Get<UiGlobalTransform>(),
                SubOrder = 1
            });
    }

    private static void AppendGlyphs(Entity entity, List<ExtractedUiNode> result)
    {
        var computed = entity.Get<ComputedNode>();
        var transform = entity.Get<UiGlobalTransform>();
        var layout = entity.Get<TextLayoutInfo>();
        var style = entity.Get<TextStyle>();
        var contentOffset = new Point(
            computed.Border.Left + computed.Padding.Left,
            computed.Border.Top + computed.Padding.Top);

        var glyphIndex = 0;
        foreach (var glyph in layout.Glyphs) {
            var location = glyph.AtlasInfo.Location;
            var glyphTopLeft = contentOffset +
                new Point(glyph.Position.X + location.OffsetX, glyph.Position.Y + location.OffsetY);
            var size = new Size(location.Width, location.Height);
            var atlas = glyph.AtlasInfo.Atlas;
            var uvMin = new Point((float)location.X / atlas.Width, (float)location.Y / atlas.Height);

            result.Add(new ExtractedUiNode(
                entity, glyphTopLeft, size, style.Color, ResolvedBorderRadius.Zero, BorderEdges.Zero,
                computed.StackIndex, atlas, uvMin,
                computed.ClipRect, transform, 2 + glyphIndex));
            glyphIndex++;
        }
    }
}

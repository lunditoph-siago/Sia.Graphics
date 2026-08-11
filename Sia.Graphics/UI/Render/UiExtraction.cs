using Sia;
using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

public static class UiExtraction
{
    public static List<ExtractedUiNode> Extract(World world)
    {
        var entities = new List<Entity>();
        using (var query = world.Query(Matchers.Of<ComputedNode, UiGlobalTransform>())) {
            foreach (var entity in query)
                entities.Add(entity);
        }
        entities.Sort((a, b) => a.Get<ComputedNode>().StackIndex.CompareTo(b.Get<ComputedNode>().StackIndex));

        var result = new List<ExtractedUiNode>(entities.Count);
        foreach (var entity in entities)
            AppendNode(entity, result);
        return result;
    }

    private static void AppendNode(Entity entity, List<ExtractedUiNode> result)
    {
        ref readonly var computed = ref entity.Get<ComputedNode>();
        var transform = entity.Get<UiGlobalTransform>();
        var topLeft = Point.Zero;
        var stackIndex = computed.StackIndex;

        if (entity.Contains<BackgroundColor>()) {
            var background = entity.Get<BackgroundColor>().Value;
            result.Add(ExtractedUiNode.SolidColor(
                topLeft, computed.Size, background, computed.BorderRadius, BorderEdges.Zero,
                ExtractedUiNodeKind.Background, stackIndex) with {
                    ClipRect = computed.ClipRect,
                    Transform = transform
                });
        }

        if (entity.Contains<BorderColor>()) {
            var border = computed.Border;
            if (border.Left > 0f || border.Top > 0f || border.Right > 0f || border.Bottom > 0f) {
                var borderColor = entity.Get<BorderColor>().Value;
                result.Add(ExtractedUiNode.SolidColor(
                    topLeft, computed.Size, borderColor, computed.BorderRadius, border,
                    ExtractedUiNodeKind.Border, stackIndex) with {
                        ClipRect = computed.ClipRect,
                        Transform = transform
                    });
            }
        }

        if (entity.Contains<TextLayoutInfo>() && entity.Contains<TextStyle>())
            AppendGlyphs(entity, computed, transform, stackIndex, result);
    }

    private static void AppendGlyphs(
        Entity entity, in ComputedNode computed, UiGlobalTransform transform, int stackIndex,
        List<ExtractedUiNode> result)
    {
        var color = entity.Get<TextStyle>().Color;
        var contentOffset = new Point(
            computed.Border.Left + computed.Padding.Left,
            computed.Border.Top + computed.Padding.Top);

        foreach (var glyph in entity.Get<TextLayoutInfo>().Glyphs) {
            var location = glyph.AtlasInfo.Location;
            var glyphTopLeft = contentOffset +
                new Point(glyph.Position.X + location.OffsetX, glyph.Position.Y + location.OffsetY);
            var size = new Size(location.Width, location.Height);
            var atlas = glyph.AtlasInfo.Atlas;
            var uvMin = new Point((float)location.X / atlas.Width, (float)location.Y / atlas.Height);
            var uvMax = new Point(
                (float)(location.X + location.Width) / atlas.Width,
                (float)(location.Y + location.Height) / atlas.Height);

            result.Add(new ExtractedUiNode(
                glyphTopLeft, size, color, ResolvedBorderRadius.Zero, BorderEdges.Zero,
                ExtractedUiNodeKind.Background, stackIndex, atlas, uvMin, uvMax,
                computed.ClipRect, transform));
        }
    }
}

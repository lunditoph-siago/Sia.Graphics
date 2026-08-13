using Sia;
using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

public static class UiExtraction
{
    public static List<ExtractedUiNode> Extract(World world)
    {
        using var backgrounds = world.Query(
            Matchers.Of<ComputedNode, UiGlobalTransform, BackgroundColor>());
        using var borders = world.Query(
            Matchers.Of<ComputedNode, UiGlobalTransform, BorderColor>());
        using var text = world.Query(
            Matchers.Of<ComputedNode, UiGlobalTransform, TextLayoutInfo, TextStyle>());
        var result = new List<ExtractedUiNode>();
        Extract(backgrounds, borders, text, result);
        return result;
    }

    internal static void Extract(
        IEntityQuery backgrounds,
        IEntityQuery borders,
        IEntityQuery text,
        List<ExtractedUiNode> result)
    {
        result.Clear();
        backgrounds.ForEach(
            result,
            static (in List<ExtractedUiNode> output, Entity entity) => {
            var computed = entity.Get<ComputedNode>();
            var transform = entity.Get<UiGlobalTransform>();
            var background = entity.Get<BackgroundColor>();
            var topLeft = Point.Zero;
            output.Add(ExtractedUiNode.SolidColor(
                topLeft, computed.Size, background.Value, computed.BorderRadius, BorderEdges.Zero,
                ExtractedUiNodeKind.Background, computed.StackIndex) with {
                    ClipRect = computed.ClipRect,
                    Transform = transform,
                    SubOrder = 0
                });
        });
        borders.ForEach(
            result,
            static (in List<ExtractedUiNode> output, Entity entity) => {
            var computed = entity.Get<ComputedNode>();
            var transform = entity.Get<UiGlobalTransform>();
            var borderColor = entity.Get<BorderColor>();
            var border = computed.Border;
            if (border.Left > 0f || border.Top > 0f || border.Right > 0f || border.Bottom > 0f) {
                output.Add(ExtractedUiNode.SolidColor(
                    Point.Zero, computed.Size, borderColor.Value, computed.BorderRadius, border,
                    ExtractedUiNodeKind.Border, computed.StackIndex) with {
                        ClipRect = computed.ClipRect,
                        Transform = transform,
                        SubOrder = 1
                    });
            }
        });
        text.ForEach(
            result,
            static (in List<ExtractedUiNode> output, Entity entity) =>
                AppendGlyphs(
                    entity.Get<ComputedNode>(),
                    entity.Get<UiGlobalTransform>(),
                    entity.Get<TextLayoutInfo>(),
                    entity.Get<TextStyle>(),
                    output));
        result.Sort(static (left, right) => {
            var stackOrder = left.StackIndex.CompareTo(right.StackIndex);
            return stackOrder != 0 ? stackOrder : left.SubOrder.CompareTo(right.SubOrder);
        });
    }

    private static void AppendGlyphs(
        in ComputedNode computed,
        UiGlobalTransform transform,
        in TextLayoutInfo layout,
        in TextStyle style,
        List<ExtractedUiNode> result)
    {
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
            var uvMax = new Point(
                (float)(location.X + location.Width) / atlas.Width,
                (float)(location.Y + location.Height) / atlas.Height);

            result.Add(new ExtractedUiNode(
                glyphTopLeft, size, style.Color, ResolvedBorderRadius.Zero, BorderEdges.Zero,
                ExtractedUiNodeKind.Background, computed.StackIndex, atlas, uvMin, uvMax,
                computed.ClipRect, transform, 2 + glyphIndex));
            glyphIndex++;
        }
    }
}

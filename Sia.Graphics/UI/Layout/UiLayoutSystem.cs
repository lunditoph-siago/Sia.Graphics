using Sia;
using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

public sealed class UiLayoutSystem() : UiInvalidatedSystem(
    Matchers.Of<Node, ComputedNode, UiGlobalTransform, UiRoot>())
{
    private readonly LayoutTree _tree = new();
    private readonly Dictionary<Entity, LayoutNodeId> _map = [];
    private readonly Dictionary<Entity, TextMeasure> _textMeasures = [];
    private readonly HashSet<Entity> _visited = [];

    protected override long GetVersion(UiChangeTracker changes) => changes.LayoutVersion;

    protected override void ExecuteInvalidated(World world, IEntityQuery query)
    {
        var atlases = world.AcquireAddon<FontAtlasSet>();

        foreach (var root in query) {
            var viewport = root.Get<UiRoot>().Viewport;
            _tree.Clear();
            _map.Clear();
            _textMeasures.Clear();
            _visited.Clear();

            var rootId = BuildSubtree(_tree, _map, _textMeasures, root);
            _tree.ComputeLayout(
                rootId,
                new AvailableSize(AvailableSpace.Definite(viewport.Width), AvailableSpace.Definite(viewport.Height)));

            WriteBack(
                _tree, _map, _textMeasures, atlases, root,
                UiGlobalTransform.Identity, viewport, null, _visited);
        }
    }

    private static LayoutNodeId BuildSubtree(
        LayoutTree tree, Dictionary<Entity, LayoutNodeId> map, Dictionary<Entity, TextMeasure> textMeasures,
        Entity entity)
    {
        ILayoutMeasure? measure = null;
        if (entity.Contains<Text>() && entity.Contains<TextStyle>()) {
            var style = entity.Get<TextStyle>();
            var textMeasure = new TextMeasure(
                style.Font,
                style.FallbackFonts,
                style.ShapingProvider,
                style.FontSize,
                entity.Get<Text>().Value);
            textMeasures[entity] = textMeasure;
            measure = textMeasure;
        }

        var id = tree.CreateNode(entity.Get<Node>(), measure);
        map[entity] = id;

        if (entity.Contains<UiChildren>()) {
            var children = entity.Get<UiChildren>().Value;
            foreach (var child in children) {
                if (!child.IsValid || !child.Contains<Node>() || map.ContainsKey(child))
                    continue;
                tree.AddChild(id, BuildSubtree(tree, map, textMeasures, child));
            }
        }

        return id;
    }

    private static void WriteBack(
        LayoutTree tree, Dictionary<Entity, LayoutNodeId> map, Dictionary<Entity, TextMeasure> textMeasures,
        FontAtlasSet atlases,
        Entity entity,
        UiGlobalTransform parentTransform,
        Size viewport,
        UiClipRect? parentClip,
        HashSet<Entity> visited)
    {
        if (!visited.Add(entity))
            return;
        var id = map[entity];
        var layout = tree.GetLayout(id);

        var world = parentTransform * UiGlobalTransform.Translation(layout.Location.X, layout.Location.Y);

        var computed = entity.Get<ComputedNode>();
        computed.Size = layout.Size;
        computed.UnroundedSize = layout.Size;
        computed.ContentSize = layout.ContentSize;
        computed.Border = layout.Border;
        computed.Padding = layout.Padding;
        computed.BorderRadius = ResolveBorderRadius(entity.Get<Node>().BorderRadius, layout.Size, viewport);
        var style = entity.Get<Node>();
        var clip = parentClip;
        if (style.Overflow.ClipsX || style.Overflow.ClipsY) {
            var ownClip = Bounds(world, layout.Size);
            var margin = style.OverflowClipMargin.ResolveOrZero(
                1f, MathF.Min(layout.Size.Width, layout.Size.Height), viewport);
            ownClip = new UiClipRect(
                ownClip.X - margin,
                ownClip.Y - margin,
                ownClip.Width + margin * 2f,
                ownClip.Height + margin * 2f);
            clip = clip is { } inherited ? inherited.Intersect(ownClip) : ownClip;
        }
        computed.ClipRect = clip;

        if (entity.Get<ComputedNode>() != computed)
            entity.Set(computed);
        if (entity.Get<UiGlobalTransform>() != world)
            entity.Set(world);

        if (textMeasures.TryGetValue(entity, out var measure) && entity.Contains<TextLayoutInfo>())
            WriteGlyphs(entity, measure, atlases);

        if (!entity.Contains<UiChildren>())
            return;

        var contentOrigin = UiGlobalTransform.Translation(
            layout.Border.Left + layout.Padding.Left,
            layout.Border.Top + layout.Padding.Top);
        var childBase = world * contentOrigin;

        foreach (var child in entity.Get<UiChildren>().Value) {
            if (map.ContainsKey(child))
                WriteBack(
                    tree, map, textMeasures, atlases, child,
                    childBase, viewport, clip, visited);
        }
    }

    private static UiClipRect Bounds(UiGlobalTransform transform, Size size)
    {
        Span<Point> corners = [
            transform.Transform(Point.Zero),
            transform.Transform(new Point(size.Width, 0f)),
            transform.Transform(new Point(size.Width, size.Height)),
            transform.Transform(new Point(0f, size.Height))
        ];
        var minX = corners[0].X;
        var maxX = corners[0].X;
        var minY = corners[0].Y;
        var maxY = corners[0].Y;
        for (var i = 1; i < corners.Length; i++) {
            minX = MathF.Min(minX, corners[i].X);
            maxX = MathF.Max(maxX, corners[i].X);
            minY = MathF.Min(minY, corners[i].Y);
            maxY = MathF.Max(maxY, corners[i].Y);
        }
        return new UiClipRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static ResolvedBorderRadius ResolveBorderRadius(
        BorderRadius radius, Size size, Size viewport)
    {
        var basis = MathF.Min(size.Width, size.Height);
        var topLeft = radius.TopLeft.ResolveOrZero(1f, basis, viewport);
        var topRight = radius.TopRight.ResolveOrZero(1f, basis, viewport);
        var bottomRight = radius.BottomRight.ResolveOrZero(1f, basis, viewport);
        var bottomLeft = radius.BottomLeft.ResolveOrZero(1f, basis, viewport);

        var scale = 1f;
        scale = MathF.Min(scale, PairScale(size.Width, topLeft + topRight));
        scale = MathF.Min(scale, PairScale(size.Width, bottomLeft + bottomRight));
        scale = MathF.Min(scale, PairScale(size.Height, topLeft + bottomLeft));
        scale = MathF.Min(scale, PairScale(size.Height, topRight + bottomRight));
        return new ResolvedBorderRadius(
            MathF.Max(0f, topLeft) * scale,
            MathF.Max(0f, topRight) * scale,
            MathF.Max(0f, bottomRight) * scale,
            MathF.Max(0f, bottomLeft) * scale);
    }

    private static float PairScale(float available, float requested) =>
        requested > 0f ? MathF.Min(1f, MathF.Max(0f, available) / requested) : 1f;

    private static void WriteGlyphs(Entity entity, TextMeasure measure, FontAtlasSet atlases)
    {
        var info = entity.Get<TextLayoutInfo>();
        var glyphs = info.Glyphs;
        glyphs.Clear();

        if (measure.LastShaped is not { } shaped) {
            entity.Set(info);
            return;
        }

        var style = entity.Get<TextStyle>();
        foreach (var glyph in shaped.Glyphs) {
            var glyphFont = glyph.Font ?? style.Font;
            var atlasInfo = atlases.GetOrAddGlyph(glyphFont, style.FontSize, glyph.GlyphId);
            if (atlasInfo.Location.Width == 0 || atlasInfo.Location.Height == 0)
                continue;
            glyphs.Add(new PositionedGlyph(
                glyph.Position,
                atlasInfo,
                glyph.Codepoint,
                glyph.GlyphId,
                glyph.UsedFallback));
        }
        entity.Set(info);
    }
}

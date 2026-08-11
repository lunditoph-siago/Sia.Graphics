namespace Sia.Graphics.UI;

public static class UiBatcher
{
    public static (UiVertex[] Vertices, List<UiBatch> Batches) Build(IReadOnlyList<ExtractedUiNode> nodes)
    {
        var vertices = new List<UiVertex>(nodes.Count * 6);
        var batches = new List<UiBatch>();

        object? currentKey = null;
        var batchStart = 0;
        var hasBatch = false;

        foreach (var node in nodes) {
            if (!TryGetVisibleQuad(node, out var quad))
                continue;
            if (hasBatch && !Equals(currentKey, node.TextureKey)) {
                batches.Add(new UiBatch(currentKey, batchStart, vertices.Count - batchStart));
                batchStart = vertices.Count;
            }
            currentKey = node.TextureKey;
            hasBatch = true;
            AppendQuad(vertices, node, quad);
        }

        if (hasBatch)
            batches.Add(new UiBatch(currentKey, batchStart, vertices.Count - batchStart));

        return (vertices.ToArray(), batches);
    }

    private static void AppendQuad(
        List<UiVertex> vertices,
        in ExtractedUiNode node,
        in VisibleQuad quad)
    {
        var flags = node.TextureKey != null ? UiVertexFlags.Textured : UiVertexFlags.None;
        if (node.Kind == ExtractedUiNodeKind.Border) {
            if (node.Border.Left > 0f)
                flags |= UiVertexFlags.BorderLeft;
            if (node.Border.Top > 0f)
                flags |= UiVertexFlags.BorderTop;
            if (node.Border.Right > 0f)
                flags |= UiVertexFlags.BorderRight;
            if (node.Border.Bottom > 0f)
                flags |= UiVertexFlags.BorderBottom;
        }

        var localCenter = new Point(
            node.TopLeft.X + node.Size.Width / 2f,
            node.TopLeft.Y + node.Size.Height / 2f);

        Span<Point> localPositions = [
            new Point(quad.Left, quad.Top),
            new Point(quad.Right, quad.Top),
            new Point(quad.Right, quad.Bottom),
            new Point(quad.Left, quad.Bottom)
        ];
        Span<Point> positions = [
            Transform(node, localPositions[0]),
            Transform(node, localPositions[1]),
            Transform(node, localPositions[2]),
            Transform(node, localPositions[3])
        ];
        Span<Point> uvs = [
            new Point(quad.UvLeft, quad.UvTop),
            new Point(quad.UvRight, quad.UvTop),
            new Point(quad.UvRight, quad.UvBottom),
            new Point(quad.UvLeft, quad.UvBottom)
        ];
        Span<Point> pointsFromCenter = [
            localPositions[0] - localCenter,
            localPositions[1] - localCenter,
            localPositions[2] - localCenter,
            localPositions[3] - localCenter
        ];

        Span<int> triangles = [0, 1, 2, 0, 2, 3];
        foreach (var i in triangles) {
            vertices.Add(UiVertex.Create(
                positions[i], uvs[i], node.Color, flags,
                node.BorderRadius, node.Border, node.Size, pointsFromCenter[i]));
        }
    }

    private static bool TryGetVisibleQuad(
        in ExtractedUiNode node, out VisibleQuad quad)
    {
        var left = node.TopLeft.X;
        var top = node.TopLeft.Y;
        var right = left + node.Size.Width;
        var bottom = top + node.Size.Height;
        if (node.ClipRect is { } clip) {
            if (node.Transform is not { } transform
                || MathF.Abs(transform.M12) <= 0.0001f && MathF.Abs(transform.M21) <= 0.0001f) {
                var clipTopLeft = node.Transform is { } value
                    ? value.InverseTransform(new Point(clip.X, clip.Y))
                    : new Point(clip.X, clip.Y);
                var clipBottomRight = node.Transform is { } inverse
                    ? inverse.InverseTransform(new Point(clip.Right, clip.Bottom))
                    : new Point(clip.Right, clip.Bottom);
                var clipLeft = MathF.Min(clipTopLeft.X, clipBottomRight.X);
                var clipRight = MathF.Max(clipTopLeft.X, clipBottomRight.X);
                var clipTop = MathF.Min(clipTopLeft.Y, clipBottomRight.Y);
                var clipBottom = MathF.Max(clipTopLeft.Y, clipBottomRight.Y);
                left = MathF.Max(left, clipLeft);
                top = MathF.Max(top, clipTop);
                right = MathF.Min(right, clipRight);
                bottom = MathF.Min(bottom, clipBottom);
            }
        }
        if (right <= left || bottom <= top || node.Size.Width <= 0f || node.Size.Height <= 0f) {
            quad = default;
            return false;
        }

        var leftRatio = (left - node.TopLeft.X) / node.Size.Width;
        var rightRatio = (right - node.TopLeft.X) / node.Size.Width;
        var topRatio = (top - node.TopLeft.Y) / node.Size.Height;
        var bottomRatio = (bottom - node.TopLeft.Y) / node.Size.Height;
        quad = new VisibleQuad(
            left, top, right, bottom,
            Lerp(node.UvMin.X, node.UvMax.X, leftRatio),
            Lerp(node.UvMin.X, node.UvMax.X, rightRatio),
            Lerp(node.UvMin.Y, node.UvMax.Y, topRatio),
            Lerp(node.UvMin.Y, node.UvMax.Y, bottomRatio));
        return true;
    }

    private static float Lerp(float start, float end, float amount) =>
        start + (end - start) * amount;

    private static Point Transform(in ExtractedUiNode node, Point point) =>
        node.Transform is { } transform ? transform.Transform(point) : point;

    private readonly record struct VisibleQuad(
        float Left,
        float Top,
        float Right,
        float Bottom,
        float UvLeft,
        float UvRight,
        float UvTop,
        float UvBottom);
}

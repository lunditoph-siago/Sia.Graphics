namespace Sia.Graphics.UI;

internal sealed class UiPrimitiveStore
{
    private readonly Dictionary<PrimitiveKey, int> _slots = [];
    private readonly HashSet<PrimitiveKey> _live = [];
    private readonly List<PrimitiveKey> _stale = [];
    private readonly Stack<int> _freeSlots = [];

    public List<UiPrimitive> Primitives { get; } = [];
    public List<uint> PaintOrder { get; } = [];

    public void Build(IReadOnlyList<ExtractedUiNode> nodes)
    {
        _live.Clear();
        PaintOrder.Clear();
        PaintOrder.EnsureCapacity(nodes.Count);

        foreach (var node in nodes) {
            if (node.Size.Width <= 0f || node.Size.Height <= 0f || !IsVisible(node))
                continue;

            var key = new PrimitiveKey(node.Owner, node.SubOrder);
            _live.Add(key);
            if (!_slots.TryGetValue(key, out var slot)) {
                if (!_freeSlots.TryPop(out slot)) {
                    slot = Primitives.Count;
                    Primitives.Add(default);
                }
                _slots.Add(key, slot);
            }
            Primitives[slot] = UiPrimitive.Create(node);
            PaintOrder.Add((uint)slot);
        }

        _stale.Clear();
        foreach (var key in _slots.Keys) {
            if (!_live.Contains(key))
                _stale.Add(key);
        }
        foreach (var key in _stale) {
            var slot = _slots[key];
            _slots.Remove(key);
            _freeSlots.Push(slot);
        }
    }

    private static bool IsVisible(in ExtractedUiNode node)
    {
        if (node.ClipRect is not { } clip)
            return true;
        if (clip.Width <= 0f || clip.Height <= 0f)
            return false;

        var transform = node.Transform ?? UiGlobalTransform.Identity;
        var topLeft = transform.Transform(node.TopLeft);
        var topRight = transform.Transform(new Point(node.TopLeft.X + node.Size.Width, node.TopLeft.Y));
        var bottomLeft = transform.Transform(new Point(node.TopLeft.X, node.TopLeft.Y + node.Size.Height));
        var bottomRight = transform.Transform(new Point(
            node.TopLeft.X + node.Size.Width,
            node.TopLeft.Y + node.Size.Height));
        var left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        var right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        var top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        var bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return right > clip.X && left < clip.Right && bottom > clip.Y && top < clip.Bottom;
    }

    private readonly record struct PrimitiveKey(Entity Owner, int SubOrder);
}

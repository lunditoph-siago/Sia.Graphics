namespace Sia.Graphics.UI;

internal sealed class UiPrimitiveStore
{
    private readonly Dictionary<PrimitiveKey, int> _slots = [];
    private readonly Dictionary<Entity, int> _slotCounts = [];
    private readonly Dictionary<Entity, int> _stackIndices = [];
    private readonly HashSet<PrimitiveKey> _live = [];
    private readonly List<PrimitiveKey> _stale = [];
    private readonly Stack<int> _freeSlots = [];

    public List<UiPrimitive> Primitives { get; } = [];
    public List<uint> PaintOrder { get; } = [];

    public void Build(IReadOnlyList<ExtractedUiNode> nodes)
    {
        _live.Clear();
        _slotCounts.Clear();
        _stackIndices.Clear();
        PaintOrder.Clear();
        PaintOrder.EnsureCapacity(nodes.Count);

        foreach (var node in nodes) {
            if (!IsRenderable(node))
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
            _slotCounts.TryGetValue(node.Owner, out var count);
            _slotCounts[node.Owner] = count + 1;
            _stackIndices[node.Owner] = node.StackIndex;
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

    public bool Update(Entity owner, IReadOnlyList<ExtractedUiNode> nodes)
    {
        var count = 0;
        var stackIndex = 0;
        foreach (var node in nodes) {
            if (!IsRenderable(node))
                continue;
            if (count == 0)
                stackIndex = node.StackIndex;
            else if (node.StackIndex != stackIndex)
                return false;
            if (!_slots.TryGetValue(new PrimitiveKey(owner, node.SubOrder), out var slot))
                return false;
            Primitives[slot] = UiPrimitive.Create(node);
            count++;
        }
        var expected = _slotCounts.TryGetValue(owner, out var slotCount) ? slotCount : 0;
        return count == expected
            && (count == 0 || _stackIndices.TryGetValue(owner, out var previousStackIndex)
                && stackIndex == previousStackIndex);
    }

    private static bool IsRenderable(in ExtractedUiNode node) =>
        node.Size.Width > 0f
        && node.Size.Height > 0f
        && node.ClipRect is not { Width: <= 0f } and not { Height: <= 0f };

    private readonly record struct PrimitiveKey(Entity Owner, int SubOrder);
}

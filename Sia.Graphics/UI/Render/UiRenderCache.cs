using Sia;

namespace Sia.Graphics.UI;

internal sealed class UiRenderCache : IAddon
{
    private readonly List<ExtractedUiNode> _nodes = [];
    private readonly List<ExtractedUiNode> _entityNodes = [];
    private readonly List<Entity> _dirtyEntities = [];
    private readonly UiPrimitiveStore _store = new();
    private UiChangeTracker? _changes;
    private IEntityQuery? _backgrounds;
    private IEntityQuery? _borders;
    private IEntityQuery? _text;
    private long _preparedVersion = -1;
    private long _preparedStructureVersion = -1;

    internal long PreparedVersion => _preparedVersion;
    internal List<UiPrimitive> Primitives => _store.Primitives;
    internal List<uint> PaintOrder => _store.PaintOrder;

    internal void ConsumeChanges(List<int> dirtySlots, out bool paintOrderDirty) =>
        _store.ConsumeChanges(dirtySlots, out paintOrderDirty);

    internal void Prepare()
    {
        var changes = _changes
            ?? throw new InvalidOperationException("The UI render cache is not attached to a world.");
        if (_preparedVersion == changes.RenderVersion)
            return;

        changes.ConsumeRenderDirtyEntities(_dirtyEntities);
        if (_preparedStructureVersion != changes.RenderStructureVersion) {
            Rebuild();
        } else {
            foreach (var entity in _dirtyEntities) {
                UiExtraction.Extract(entity, _entityNodes);
                if (!_store.Update(entity, _entityNodes)) {
                    Rebuild();
                    break;
                }
            }
        }
        _preparedVersion = changes.RenderVersion;
        _preparedStructureVersion = changes.RenderStructureVersion;
    }

    private void Rebuild()
    {
        UiExtraction.Extract(_backgrounds!, _borders!, _text!, _nodes);
        _store.Build(_nodes);
    }

    void IAddon.OnInitialize(World world)
    {
        _changes = world.AcquireAddon<UiChangeTracker>();
        _backgrounds = world.Query(Matchers.Of<ComputedNode, UiGlobalTransform, BackgroundColor>());
        _borders = world.Query(Matchers.Of<ComputedNode, UiGlobalTransform, BorderColor>());
        _text = world.Query(Matchers.Of<ComputedNode, UiGlobalTransform, TextLayoutInfo, TextStyle>());
    }

    void IAddon.OnUninitialize(World world)
    {
        _backgrounds?.Dispose();
        _borders?.Dispose();
        _text?.Dispose();
        _backgrounds = null;
        _borders = null;
        _text = null;
        _changes = null;
    }
}

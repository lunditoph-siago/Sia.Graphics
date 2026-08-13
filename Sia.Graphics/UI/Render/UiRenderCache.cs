using Sia;

namespace Sia.Graphics.UI;

internal sealed class UiRenderCache : IAddon
{
    private readonly List<ExtractedUiNode> _nodes = [];
    private readonly List<UiVertex> _vertices = [];
    private readonly List<UiBatch> _batches = [];
    private UiChangeTracker? _changes;
    private IEntityQuery? _backgrounds;
    private IEntityQuery? _borders;
    private IEntityQuery? _text;
    private long _preparedVersion = -1;

    internal List<UiBatch> Batches => _batches;
    internal long PreparedVersion => _preparedVersion;
    internal List<UiVertex> VertexStorage => _vertices;

    internal bool Prepare()
    {
        var changes = _changes
            ?? throw new InvalidOperationException("The UI render cache is not attached to a world.");
        if (_preparedVersion == changes.RenderVersion)
            return false;

        UiExtraction.Extract(_backgrounds!, _borders!, _text!, _nodes);
        UiBatcher.Build(_nodes, _vertices, _batches);
        _preparedVersion = changes.RenderVersion;
        return true;
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

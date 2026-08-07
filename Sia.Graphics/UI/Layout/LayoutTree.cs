using System.Runtime.InteropServices;

namespace Sia.Graphics.UI;

public sealed class LayoutTree
{
    private readonly List<Node> _styles = [];
    private readonly List<List<LayoutNodeId>> _children = [];
    private readonly List<ILayoutMeasure?> _measures = [];
    private readonly List<LayoutResult> _results = [];

    public LayoutNodeId CreateNode(Node style, ILayoutMeasure? measure = null)
    {
        var id = new LayoutNodeId(_styles.Count);
        _styles.Add(style);
        _children.Add([]);
        _measures.Add(measure);
        _results.Add(LayoutResult.Zero);
        return id;
    }

    public Node GetStyle(LayoutNodeId id) => _styles[id.Value];

    public void SetStyle(LayoutNodeId id, Node style) => _styles[id.Value] = style;

    public void SetMeasure(LayoutNodeId id, ILayoutMeasure? measure) => _measures[id.Value] = measure;

    public float? GetBaseline(LayoutNodeId id) => _measures[id.Value]?.Baseline;

    public IReadOnlyList<LayoutNodeId> GetChildren(LayoutNodeId id) => _children[id.Value];

    public void SetChildren(LayoutNodeId id, IReadOnlyList<LayoutNodeId> children)
    {
        var list = _children[id.Value];
        list.Clear();
        list.AddRange(children);
    }

    public ref readonly LayoutResult GetLayout(LayoutNodeId id) => ref CollectionsMarshal.AsSpan(_results)[id.Value];

    public void ComputeLayout(LayoutNodeId root, AvailableSize availableSpace, float scaleFactor = 1f)
    {
        var viewport = new Size(
            availableSpace.Width.UnwrapOr(0f),
            availableSpace.Height.UnwrapOr(0f));

        var input = new LayoutInput(
            KnownDimensions: new PartialSize(
                availableSpace.Width.IsDefinite ? availableSpace.Width.Value : null,
                availableSpace.Height.IsDefinite ? availableSpace.Height.Value : null),
            ParentSize: PartialSize.Unknown,
            AvailableSpace: availableSpace,
            Viewport: viewport,
            ScaleFactor: scaleFactor,
            PerformLayout: true);

        var size = ComputeNodeSize(root, input);
        ref var result = ref CollectionsMarshal.AsSpan(_results)[root.Value];
        result.Location = Point.Zero;
        result.Size = size;
    }

    public Size ComputeNodeSize(LayoutNodeId id, LayoutInput input)
    {
        var measure = _measures[id.Value];
        if (measure != null)
            return LeafLayout.Compute(this, id, measure, input);

        var style = _styles[id.Value];
        var size = style.Display switch {
            Display.None => Size.Zero,
            Display.Flex => FlexboxLayout.Compute(this, id, input),
            Display.Grid => GridLayout.Compute(this, id, input),
            _ => BlockLayout.Compute(this, id, input)
        };
        if (input.PerformLayout && style.Display != Display.None)
            AbsoluteLayout.ComputeChildren(this, id, input);
        return size;
    }

    internal void SetChildLayout(LayoutNodeId id, LayoutResult layout) =>
        CollectionsMarshal.AsSpan(_results)[id.Value] = layout;
}

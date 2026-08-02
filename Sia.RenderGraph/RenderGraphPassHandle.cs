namespace Sia.RenderGraph;

public readonly record struct RenderGraphPassHandle
{
    private readonly int _graphId;
    private readonly int _index;

    internal RenderGraphPassHandle(int graphId, int index)
    {
        _graphId = graphId;
        _index = index;
    }

    public bool IsValid => _graphId != 0;

    internal int GraphId => _graphId;

    internal int Index => _index;
}

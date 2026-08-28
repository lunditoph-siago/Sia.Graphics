namespace Sia.RenderGraph;

public readonly record struct RenderGraphPassGroup(
    int StartExecutionIndex,
    int Count)
{
    public int EndExecutionIndex => StartExecutionIndex + Count;
}

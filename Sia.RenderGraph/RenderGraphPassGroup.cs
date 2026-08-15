namespace Sia.RenderGraph;

/// <summary>
/// A contiguous run of <see cref="CompiledRenderGraph.Passes"/> (identified by execution-order
/// range) that write the exact same render-attachment texture set with no other hazard between
/// them. A backend is free to execute every pass in a group inside a single physical render
/// pass instead of one per pass — see <see cref="CompiledRenderGraph.PassGroups"/>. Grouping is
/// purely an execution-boundary optimization: it never changes scheduling order, resource
/// lifetimes, or the compiled dependency graph, and a backend that ignores it (running one
/// physical pass per <see cref="CompiledRenderGraphPass"/> regardless) still produces correct
/// output.
/// </summary>
public readonly record struct RenderGraphPassGroup(
    int StartExecutionIndex,
    int Count)
{
    public int EndExecutionIndex => StartExecutionIndex + Count;
}

namespace Sia.Spirv.Compiler.IL;

/// <summary>
/// Wraps a <see cref="CilControlFlowGraph"/> with the analysis every
/// consumer builds on top of it, without mutating or replacing the graph
/// itself: which blocks are actually reachable from the entry block, and
/// (lazily, via <see cref="CilCallResolver"/>) what a given call site
/// resolves to. <see cref="CilInstruction"/>/<see cref="CilBasicBlock"/>/
/// <see cref="CilControlFlowGraph"/> stay plain records of the raw CIL
/// facts; reachability and call identity are derived, on-demand analysis
/// results, not fields bolted onto the instructions themselves.
/// </summary>
public sealed class ShaderCilView
{
    private readonly HashSet<int> _reachableBlockIds;

    public ShaderCilView(CilControlFlowGraph graph, CilCallResolver resolver)
    {
        Graph = graph;
        Resolver = resolver;
        _reachableBlockIds = ComputeReachableBlockIds(graph);
    }

    public CilControlFlowGraph Graph { get; }

    public CilCallResolver Resolver { get; }

    public IEnumerable<CilBasicBlock> ReachableBlocks =>
        Graph.Blocks.Where(block => _reachableBlockIds.Contains(block.Id));

    public bool IsReachable(CilBasicBlock block) => _reachableBlockIds.Contains(block.Id);

    /// <summary>
    /// Resolves the <c>call</c>/<c>callvirt</c> instruction at
    /// <paramref name="instructionIndex"/> within <paramref name="block"/>.
    /// </summary>
    public ResolvedCall ResolveCall(CilBasicBlock block, int instructionIndex) =>
        Resolver.Resolve((int)block.Instructions[instructionIndex].Operand!);

    private static HashSet<int> ComputeReachableBlockIds(CilControlFlowGraph graph)
    {
        var reachable = new HashSet<int>();
        if (graph.Blocks.Count == 0) {
            return reachable;
        }

        var pending = new Queue<int>();
        reachable.Add(0);
        pending.Enqueue(0);
        while (pending.TryDequeue(out var blockId)) {
            foreach (var successor in graph.Blocks[blockId].Successors) {
                if (reachable.Add(successor)) {
                    pending.Enqueue(successor);
                }
            }
        }

        return reachable;
    }
}

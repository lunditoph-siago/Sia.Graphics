namespace Sia.Spirv.Compiler.IL;

/// <summary>
/// Wraps a <see cref="CilControlFlowGraph"/> with derived analysis — which
/// blocks are reachable from the entry block, and (lazily) what a call
/// site resolves to — without mutating the graph itself.
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

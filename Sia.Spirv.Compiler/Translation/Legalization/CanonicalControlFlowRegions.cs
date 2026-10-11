using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// Reconstruct reducible regions on owned target graphs. Source CIL topology and
// SSA remain independent of the merge/continuing constraints of either output.
internal static class CanonicalControlFlowRegions
{
    public static CanonicalModule Run(CanonicalModule input)
    {
        var result = input with { Functions = input.Functions.ToDictionary(p => p.Key, p => Run(p.Value), StringComparer.Ordinal) };
        ModuleValidator.Validate(result, native: true); return result;
    }

    internal static ControlFlowFunction Run(ControlFlowFunction input)
    {
        var graph = input.Copy(); SplitPartialJoins(graph); var blocks = graph.Blocks.ToDictionary(b => b.Id);
        var dominance = ControlFlowAnalysis.Dominators(graph); var predecessors = ControlFlowAnalysis.Predecessors(graph);
        var backedges = graph.Blocks.SelectMany(b => b.Terminator!.Edges.Where(e => dominance[b.Id].Contains(e.Target)).Select(e => (Source: b.Id, Target: e.Target))).ToHashSet();
        var degrees = graph.Blocks.ToDictionary(b => b.Id, _ => 0);
        foreach (var block in graph.Blocks) foreach (var edge in block.Terminator!.Edges) if (!backedges.Contains((block.Id, edge.Target))) degrees[edge.Target]++;
        var pending = new Queue<int>(degrees.Where(p => p.Value == 0).Select(p => p.Key)); int count = 0;
        while (pending.TryDequeue(out int id)) {
            count++;
            foreach (var edge in blocks[id].Terminator!.Edges) if (!backedges.Contains((id, edge.Target)) && --degrees[edge.Target] == 0) pending.Enqueue(edge.Target);
        }
        if (count != blocks.Count) return CanonicalControlFlowDispatcher.Run(input);
        graph.Loops.Clear(); graph.SelectionMerges.Clear();
        var loops = new Dictionary<int, HashSet<int>>();
        foreach (var group in backedges.GroupBy(e => e.Target)) {
            int header = group.Key; var nodes = new HashSet<int> { header }; var todo = new Stack<int>(group.Select(e => e.Source));
            while (todo.TryPop(out int id)) if (nodes.Add(id)) foreach (var predecessor in predecessors[id]) todo.Push(predecessor.Block.Id);
            var continuing = graph.Block();
            foreach (var parameter in blocks[header].Parameters) continuing.Parameters.Add(graph.Value(parameter.Type));
            continuing.Terminator = new ControlFlowTerminator.Branch(new(header, continuing.Parameters));
            foreach (var block in blocks.Values) {
                ControlFlowEdge Edge(ControlFlowEdge edge) => backedges.Contains((block.Id, edge.Target)) && edge.Target == header
                    ? new(continuing.Id, edge.Arguments) : edge;
                block.Terminator = block.Terminator switch {
                    ControlFlowTerminator.Branch b => b with { Edge = Edge(b.Edge) },
                    ControlFlowTerminator.Conditional c => c with { Accept = Edge(c.Accept), Reject = Edge(c.Reject) },
                    ControlFlowTerminator.Switch s => s with { Cases = s.Cases.Select(c => c with { Edge = Edge(c.Edge) }).ToArray(), Default = Edge(s.Default) },
                    _ => block.Terminator
                };
            }
            nodes.Add(continuing.Id); loops.Add(header, nodes); graph.Loops.Add(header, new(continuing.Id, null));
        }
        blocks = graph.Blocks.ToDictionary(b => b.Id); dominance = ControlFlowAnalysis.Dominators(graph);
        var full = Postdominators(graph, blocks.Keys.ToHashSet(), []);
        int? Nearest(int header, Dictionary<int, HashSet<int>> postdominators, Func<int, bool> eligible)
            => postdominators[header].Where(id => id >= 0 && id != header && eligible(id) && dominance[id].Contains(header))
                .OrderByDescending(id => postdominators[id].Count).ThenBy(id => id).Cast<int?>().FirstOrDefault();
        foreach (var (header, nodes) in loops) graph.Loops[header] = graph.Loops[header] with {
            Merge = Nearest(header, full, id => !nodes.Contains(id))
        };
        foreach (var block in graph.Blocks.Where(b => b.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch)) {
            if (graph.Loops.ContainsKey(block.Id)) {
                // A conditional header may reach the loop exit through an edge-copy
                // block. Both arms then end in break/continue rather than at a
                // selection join; keep that selection inside the loop body.
                graph.SelectionMerges.Add(block.Id, null); continue;
            }
            var loop = loops.Where(p => p.Value.Contains(block.Id)).OrderBy(p => p.Value.Count).FirstOrDefault();
            var postdominators = loop.Value is null ? full : Postdominators(graph, loop.Value,
                [graph.Loops[loop.Key].Continuing!.Value]);
            graph.SelectionMerges.Add(block.Id, Nearest(block.Id, postdominators, _ => true));
        }
        return graph;
    }

    private static void SplitPartialJoins(ControlFlowFunction graph)
    {
        bool changed;
        do {
            changed = false;
            var dominance = ControlFlowAnalysis.Dominators(graph); var predecessors = ControlFlowAnalysis.Predecessors(graph);
            var postdominators = Postdominators(graph, graph.Blocks.Select(b => b.Id).ToHashSet(), []);
            foreach (var block in graph.Blocks.ToArray()) {
                var incoming = predecessors[block.Id];
                if (incoming.Count < 2 || incoming.Any(p => dominance[p.Block.Id].Contains(block.Id))
                    || block.Terminator is not ControlFlowTerminator.Branch
                    || block.Instructions.Any(i => i.Operation is ValueOperation.Local)) continue;
                var common = new HashSet<int>(dominance[incoming[0].Block.Id]);
                foreach (var predecessor in incoming.Skip(1)) common.IntersectWith(dominance[predecessor.Block.Id]);
                int header = common.OrderByDescending(id => dominance[id].Count).First();
                if (postdominators[header].Contains(block.Id)) continue;
                var definitions = block.Parameters.Concat(block.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)).Select(v => v.Id).ToHashSet();
                if (graph.Blocks.Where(b => b.Id != block.Id).SelectMany(b => b.Instructions.SelectMany(i => i.Operation.Operands)
                    .Concat(b.Terminator!.Operands).Concat(b.Terminator.Edges.SelectMany(e => e.Arguments))).Any(v => definitions.Contains(v.Id))) continue;
                foreach (var (parent, edge) in incoming.Skip(1)) {
                    var copy = graph.Block(); var values = new Dictionary<int, SsaValue>();
                    foreach (var parameter in block.Parameters) { var value = graph.Value(parameter.Type); values.Add(parameter.Id, value); copy.Parameters.Add(value); }
                    foreach (var instruction in block.Instructions) if (instruction.Result is { } value) values.Add(value.Id, graph.Value(value.Type));
                    SsaValue Value(SsaValue value) => values.GetValueOrDefault(value.Id, value);
                    copy.Instructions.AddRange(block.Instructions.Select(i => i with { Result = i.Result is { } value ? Value(value) : null, Operation = i.Operation.Map(Value) }));
                    var branch = (ControlFlowTerminator.Branch)block.Terminator;
                    copy.Terminator = new ControlFlowTerminator.Branch(new(branch.Edge.Target, branch.Edge.Arguments.Select(Value)));
                    ControlFlowEdge Edge(ControlFlowEdge candidate) => ReferenceEquals(candidate, edge) ? new(copy.Id, candidate.Arguments) : candidate;
                    parent.Terminator = parent.Terminator switch {
                        ControlFlowTerminator.Branch b => b with { Edge = Edge(b.Edge) },
                        ControlFlowTerminator.Conditional c => c with { Accept = Edge(c.Accept), Reject = Edge(c.Reject) },
                        ControlFlowTerminator.Switch s => s with { Cases = s.Cases.Select(c => c with { Edge = Edge(c.Edge) }).ToArray(), Default = Edge(s.Default) },
                        _ => parent.Terminator
                    };
                }
                changed = true; break;
            }
        } while (changed);
    }

    private static Dictionary<int, HashSet<int>> Postdominators(ControlFlowFunction graph, HashSet<int> scope, HashSet<int> stops)
    {
        const int exit = -1; var blocks = graph.Blocks.Where(b => scope.Contains(b.Id)).ToDictionary(b => b.Id);
        var successors = blocks.Values.ToDictionary(b => b.Id, b => stops.Contains(b.Id) || !b.Terminator!.Edges.Any()
            ? new[] { exit } : b.Terminator.Edges.Select(e => scope.Contains(e.Target) ? e.Target : exit).Distinct().ToArray());
        var terminating = new HashSet<int> { exit }; bool changed;
        do {
            changed = false;
            foreach (var (id, outgoing) in successors) if (outgoing.Any(terminating.Contains) && terminating.Add(id)) changed = true;
        } while (changed);
        foreach (int id in scope.Where(id => !terminating.Contains(id))) successors[id] = [exit];
        var all = scope.Append(exit).ToHashSet(); var result = scope.ToDictionary(id => id, _ => new HashSet<int>(all)); result.Add(exit, [exit]);
        do {
            changed = false;
            foreach (var (id, outgoing) in successors) {
                var values = new HashSet<int>(result[outgoing[0]]);
                foreach (int successor in outgoing.Skip(1)) values.IntersectWith(result[successor]);
                values.Add(id);
                if (!result[id].SetEquals(values)) { result[id] = values; changed = true; }
            }
        } while (changed);
        return result;
    }
}

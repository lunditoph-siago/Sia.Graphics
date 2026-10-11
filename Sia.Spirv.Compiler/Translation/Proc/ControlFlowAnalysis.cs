using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static class ControlFlowAnalysis
{
    /// <summary>Shader entry closure, including calls inside expressions and continuing blocks.</summary>
    public static HashSet<string> EntryFunctions(Module module, IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs = null)
        => CalledFunctions(module, module.Functions.Where(f => f.Stage is not null).Select(f => f.Name), frontendGraphs);

    public static HashSet<string> CalledFunctions(Module module, IEnumerable<string> roots,
        IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs = null)
    {
        var functions = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(roots);
        while (pending.TryDequeue(out string? name)) {
            if (!reachable.Add(name)) continue;
            var calls = frontendGraphs?.TryGetValue(name, out var graph) == true
                ? graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>().Select(c => c.Function)
                : Calls(functions[name].Body);
            foreach (string callee in calls) if (functions.ContainsKey(callee)) pending.Enqueue(callee);
        }
        return reachable;
    }

    internal static IEnumerable<string> Calls(Block body)
    {
        foreach (var statement in body.Statements) {
            IEnumerable<Expression> values = statement switch {
                Statement.Declare { Initializer: { } value } => [value], Statement.Store s => [s.Target, s.Value],
                Statement.Evaluate e => [e.Value], Statement.If i => [i.Condition],
                Statement.Loop { BreakIf: { } value } => [value], Statement.Switch s => [s.Selector],
                Statement.Return { Value: { } value } => [value], _ => []
            };
            foreach (string name in values.SelectMany(Calls)) yield return name;
            IEnumerable<Block> children = statement switch {
                Statement.Nested n => [n.Body], Statement.If i => [i.Accept, i.Reject],
                Statement.Loop l => [l.Body, l.Continuing], Statement.Switch s => s.Cases.Select(c => c.Body), _ => []
            };
            foreach (string name in children.SelectMany(Calls)) yield return name;
        }
    }

    private static IEnumerable<string> Calls(Expression expression)
    {
        if (expression is Expression.Call { Binding: CallBinding.Function } call) yield return call.Function;
        IEnumerable<Expression> children = expression switch {
            Expression.Load l => [l.Pointer], Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right],
            Expression.Call c => c.Arguments, Expression.Construct c => c.Components, Expression.Convert c => [c.Operand],
            Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
            Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
        };
        foreach (string name in children.SelectMany(Calls)) yield return name;
    }

    public static HashSet<int> Reachable(ControlFlowFunction function)
    {
        var blocks = function.Blocks.ToDictionary(b => b.Id);
        var visited = new HashSet<int>(); var queue = new Queue<int>(); queue.Enqueue(function.Entry);
        while (queue.TryDequeue(out int id)) {
            if (!visited.Add(id)) continue;
            foreach (var edge in blocks[id].Terminator?.Edges ?? []) queue.Enqueue(edge.Target);
        }
        return visited;
    }
    public static Dictionary<int, List<(ControlFlowBlock Block, ControlFlowEdge Edge)>> Predecessors(ControlFlowFunction function)
    {
        var result = function.Blocks.ToDictionary(b => b.Id, _ => new List<(ControlFlowBlock, ControlFlowEdge)>());
        foreach (var block in function.Blocks)
            foreach (var edge in block.Terminator?.Edges ?? []) result[edge.Target].Add((block, edge));
        return result;
    }
    public static Dictionary<int, HashSet<int>> Dominators(ControlFlowFunction function)
        => Dominators(function, Predecessors(function).ToDictionary(p => p.Key,
            p => (IReadOnlyList<(ControlFlowBlock Block, ControlFlowEdge Edge)>)p.Value));

    internal static Dictionary<int, HashSet<int>> Dominators(ControlFlowFunction function,
        IReadOnlyDictionary<int, IReadOnlyList<(ControlFlowBlock Block, ControlFlowEdge Edge)>> predecessors)
    {
        var all = function.Blocks.Select(b => b.Id).ToHashSet();
        var result = function.Blocks.ToDictionary(b => b.Id, b => b.Id == function.Entry ? new HashSet<int> { b.Id } : new HashSet<int>(all));
        bool changed;
        do {
            changed = false;
            foreach (var block in function.Blocks.Where(b => b.Id != function.Entry)) {
                var incoming = predecessors[block.Id];
                var set = incoming.Count == 0 ? new HashSet<int>() : new HashSet<int>(result[incoming[0].Block.Id]);
                foreach (var predecessor in incoming.Skip(1)) set.IntersectWith(result[predecessor.Block.Id]);
                set.Add(block.Id);
                if (!result[block.Id].SetEquals(set)) { result[block.Id] = set; changed = true; }
            }
        } while (changed);
        return result;
    }
    public static void RemoveUnreachable(ControlFlowFunction function)
    {
        var reachable = Reachable(function); function.Blocks.RemoveAll(b => !reachable.Contains(b.Id));
        foreach (int header in function.SelectionMerges.Keys.ToArray()) {
            if (!reachable.Contains(header)) function.SelectionMerges.Remove(header);
            else if (function.SelectionMerges[header] is int merge && !reachable.Contains(merge)) function.SelectionMerges[header] = null;
        }
        foreach (int header in function.Loops.Keys.ToArray()) {
            if (!reachable.Contains(header)) function.Loops.Remove(header);
            else {
                var loop = function.Loops[header];
                function.Loops[header] = loop with {
                    Continuing = loop.Continuing is int continuing && reachable.Contains(continuing) ? continuing : null,
                    Merge = loop.Merge is int merge && reachable.Contains(merge) ? merge : null
                };
            }
        }
    }

    /// <summary>Branch predicates controlling each block, with a virtual function exit.
    /// Non-returning natural backedges end one iteration for postdominance only;
    /// the actual edges remain in the dependency traversal.</summary>
    public static Dictionary<int, HashSet<int>> ControlDependencies(ControlFlowFunction function)
    {
        const int exit = -1;
        var blocks = function.Blocks.ToDictionary(b => b.Id);
        var predecessors = Predecessors(function);
        var canReturn = new HashSet<int>();
        var pending = new Queue<int>(function.Blocks.Where(b => b.Terminator is ControlFlowTerminator.Return).Select(b => b.Id));
        while (pending.TryDequeue(out int id)) {
            if (!canReturn.Add(id)) continue;
            foreach (var predecessor in predecessors[id]) pending.Enqueue(predecessor.Block.Id);
        }
        var dominators = Dominators(function);
        var successors = function.Blocks.ToDictionary(b => b.Id, b => !b.Terminator!.Edges.Any() ? new[] { exit }
            : b.Terminator!.Edges.Select(e => !canReturn.Contains(b.Id) && dominators[b.Id].Contains(e.Target) ? exit : e.Target).Distinct().ToArray());
        // An irreducible non-returning component may have no natural backedge.
        // Keep it conservative rather than inferring reconvergence from an unanchored fixed point.
        var reachesExit = new HashSet<int> { exit };
        bool changed;
        do {
            changed = false;
            foreach (var pair in successors) if (pair.Value.Any(reachesExit.Contains)) changed |= reachesExit.Add(pair.Key);
        } while (changed);
        foreach (int id in blocks.Keys.Where(id => !reachesExit.Contains(id))) successors[id] = successors[id].Append(exit).ToArray();
        var all = blocks.Keys.Append(exit).ToHashSet();
        var postdominators = blocks.Keys.ToDictionary(id => id, _ => new HashSet<int>(all)); postdominators.Add(exit, [exit]);
        do {
            changed = false;
            foreach (int id in blocks.Keys) {
                var outgoing = successors[id]; var set = new HashSet<int>(postdominators[outgoing[0]]);
                foreach (int successor in outgoing.Skip(1)) set.IntersectWith(postdominators[successor]);
                set.Add(id);
                if (!postdominators[id].SetEquals(set)) { postdominators[id] = set; changed = true; }
            }
        } while (changed);
        var result = blocks.Keys.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var branch in function.Blocks.Where(b => b.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch)) {
            int merge = postdominators[branch.Id].Where(id => id != branch.Id).MaxBy(id => postdominators[id].Count);
            // Postdominance alone can mistake "continue on one arm" for a join:
            // that arm reaches the join only in a later iteration. Such divergence
            // persists through continuing/backedges until the whole loop reconverges.
            var seen = new HashSet<int>(); var probe = new Stack<int>(branch.Terminator!.Edges.Select(e => e.Target));
            bool crossesIteration = function.SelectionMerges.TryGetValue(branch.Id, out var selectionMerge) && selectionMerge != merge
                && function.Loops.Any(p => dominators[branch.Id].Contains(p.Key) && (p.Value.Continuing == merge || p.Key == merge));
            while (probe.TryPop(out int id)) {
                if (id == merge || !seen.Add(id)) continue;
                foreach (var edge in blocks[id].Terminator!.Edges) {
                    if (dominators[id].Contains(edge.Target) && dominators[branch.Id].Contains(edge.Target)) crossesIteration = true;
                    else probe.Push(edge.Target);
                }
            }
            if (crossesIteration) {
                var enclosing = function.Loops.Where(p => dominators[branch.Id].Contains(p.Key)).OrderByDescending(p => dominators[p.Key].Count).ToArray();
                merge = exit;
                foreach (var loop in enclosing) {
                    var region = new HashSet<int>(); var regionWork = new Stack<int>(); regionWork.Push(loop.Key);
                    while (regionWork.TryPop(out int id)) {
                        if (id == loop.Value.Merge || !region.Add(id)) continue;
                        foreach (var edge in blocks[id].Terminator!.Edges) regionWork.Push(edge.Target);
                    }
                    if (!region.Contains(branch.Id)) continue;
                    if (loop.Value.Merge is { } loopMerge && !region.Any(id => !blocks[id].Terminator!.Edges.Any())) merge = loopMerge;
                    break;
                }
            }
            var visited = new HashSet<int>(); var work = new Stack<int>(branch.Terminator!.Edges.Select(e => e.Target));
            while (work.TryPop(out int id)) {
                if (id == merge || !visited.Add(id)) continue;
                result[id].Add(branch.Id);
                foreach (var edge in blocks[id].Terminator!.Edges) work.Push(edge.Target);
            }
        }
        return result;
    }
}

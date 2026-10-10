using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Prepare terminating continue constructs while retaining invocation termination.
/// SPIR-V uses owned CFG edges; the remaining structured adapter serves WGSL and deferrals.</summary>
internal static class InvocationTerminationControlFlow
{
    internal static CanonicalModule PrepareSpirv(CanonicalModule input, DiagnosticStage stage)
    {
        ModuleValidator.Validate(input, native: true);
        var graphs = input.Functions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var deferred = input.DeferredFunctions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var effects = ShaderEffectAnalysis.Compute(input);
        foreach (var function in input.Declarations.Functions) {
            // Only an explicit deferred body may cross this reader boundary.
            if (graphs.ContainsKey(function.Name) || !NeedsRelocation(function.Body)) continue;
            if (!StructuredControlFlowReader.TryRead(function, input.Declarations, out var graph, out var reason, effects, native: true))
                throw new ShaderException(stage, "Invocation termination relocation requires canonical control flow: " + reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); graphs.Add(function.Name, graph!); deferred.Remove(function.Name);
        }
        bool changed = graphs.Count != input.Functions.Count;
        effects = ShaderEffectAnalysis.Compute(input.Declarations, graphs);
        foreach (var pair in graphs.ToArray()) {
            var graph = pair.Value;
            var blocks = graph.Blocks.ToDictionary(b => b.Id);
            bool ContainsKill(int start, int header, int? merge) {
                var visited = new HashSet<int>(); var pending = new Stack<int>(); pending.Push(start);
                while (pending.TryPop(out int id)) {
                    if (id == header || id == merge || !visited.Add(id)) continue;
                    var block = blocks[id];
                    if (block.Terminator is ControlFlowTerminator.InvocationKill) return true;
                    foreach (var edge in block.Terminator!.Edges) pending.Push(edge.Target);
                }
                return false;
            }
            var affected = graph.Loops.Where(p => p.Value.Continuing is int continuing && ContainsKill(continuing, p.Key, p.Value.Merge))
                .Select(p => p.Key).ToArray();
            if (affected.Length == 0) continue;
            graph = graph.Copy(); changed = true;
            foreach (int header in affected) {
                var analyses = new ControlFlowAnalysisContext(graph);
                int oldContinuing = graph.Loops[header].Continuing!.Value;
                bool BypassesMerge(int start, int? merge) {
                    var seen = new HashSet<int>(); var work = new Stack<int>(); work.Push(start);
                    while (work.TryPop(out int id)) {
                        if (id == oldContinuing) return true;
                        if (id == header || id == graph.Loops[header].Merge || id == merge || !seen.Add(id)) continue;
                        foreach (var edge in graph.Blocks.Single(b => b.Id == id).Terminator!.Edges) work.Push(edge.Target);
                    }
                    return false;
                }
                // Body continue edges used to leave nested selections legally.
                // Once the old continuing is body code, give each such selection
                // a distinct convergence block. Process inner selections first;
                // an outer join then forwards the inner join's evaluated values.
                var selections = graph.SelectionMerges.Where(p => p.Value != oldContinuing
                    && analyses.Dominance[p.Key].Contains(header) && !analyses.Dominance[p.Key].Contains(oldContinuing)
                    && graph.Blocks.Single(b => b.Id == p.Key).Terminator!.Edges.Any(e => BypassesMerge(e.Target, p.Value)))
                    .OrderByDescending(p => analyses.Dominance[p.Key].Count).Select(p => p.Key).ToArray();
                foreach (int selection in selections) {
                    analyses = new ControlFlowAnalysisContext(graph);
                    var join = graph.Block();
                    join.Parameters.AddRange(graph.Blocks.Single(b => b.Id == oldContinuing).Parameters.Select(p => graph.Value(p.Type)));
                    join.Terminator = new ControlFlowTerminator.Branch(new(oldContinuing, join.Parameters));
                    foreach (var block in graph.Blocks.Where(b => b != join && analyses.Dominance[b.Id].Contains(selection)))
                        block.Terminator = Redirect(block.Terminator!, oldContinuing, join.Id);
                    graph.SelectionMerges[selection] = join.Id;
                }
                analyses = new ControlFlowAnalysisContext(graph);
                var backedges = analyses.Predecessors[header].Where(p => analyses.Dominance[p.Block.Id].Contains(header)).ToArray();
                int? continuing = null;
                if (backedges.Length != 0) {
                    var latch = graph.Block();
                    latch.Parameters.AddRange(graph.Blocks.Single(b => b.Id == header).Parameters.Select(p => graph.Value(p.Type)));
                    latch.Terminator = new ControlFlowTerminator.Branch(new(header, latch.Parameters));
                    foreach (var block in backedges.Select(p => p.Block).Distinct()) {
                        int target = latch.Id;
                        // A former break-if is now in the loop body. Give its
                        // selection a merge inside that body, before the new
                        // continue target, retaining both exit argument sets.
                        if (block.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch
                            && !graph.SelectionMerges.ContainsKey(block.Id)) {
                            var join = graph.Block();
                            join.Parameters.AddRange(latch.Parameters.Select(p => graph.Value(p.Type)));
                            join.Terminator = new ControlFlowTerminator.Branch(new(latch.Id, join.Parameters));
                            graph.SelectionMerges.Add(block.Id, join.Id); target = join.Id;
                        }
                        block.Terminator = Redirect(block.Terminator!, header, target);
                    }
                    continuing = latch.Id;
                }
                // The old continuing instructions become ordinary loop-body
                // blocks. Their edges, effects and SSA values stay in place.
                graph.Loops[header] = graph.Loops[header] with { Continuing = continuing };
            }
            ControlFlowVerifier.Validate(graph, input.Declarations, calleeEffects: effects); graphs[pair.Key] = graph;
        }
        if (!changed) return input;
        var result = new CanonicalModule(input.Declarations, graphs, deferred, input.EntryFunctions);
        ModuleValidator.Validate(result, native: true); return result;
    }

    private static ControlFlowTerminator Redirect(ControlFlowTerminator terminator, int original, int target)
    {
        ControlFlowEdge Edge(ControlFlowEdge edge) => edge.Target == original ? new(target, edge.Arguments) : edge;
        return terminator switch {
            ControlFlowTerminator.Branch b => b with { Edge = Edge(b.Edge) },
            ControlFlowTerminator.Conditional c => c with { Accept = Edge(c.Accept), Reject = Edge(c.Reject) },
            ControlFlowTerminator.Switch s => s with { Cases = s.Cases.Select(c => c with { Edge = Edge(c.Edge) }).ToArray(), Default = Edge(s.Default) },
            _ => terminator
        };
    }

    public static Module Run(Module input, DiagnosticStage stage)
    {
        if (!input.Functions.Any(f => NeedsRelocation(f.Body))) return input;
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions) {
            if (!NeedsRelocation(function.Body)) { output.Functions.Add(function); continue; }
            if (!StructuredControlFlowReader.TryRead(function, input, out var graph, out var reason))
                throw new ShaderException(stage, "Invocation termination relocation requires canonical control flow: " + reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, input);
            output.Functions.Add(StructuredControlFlowLowering.Run(graph!, input, relocateTerminatingContinuing: true));
        }
        ModuleValidator.Validate(output);
        return output;
    }

    private static bool ContainsKill(Block body) => body.Statements.Any(s => s switch {
        Statement.InvocationKill => true, Statement.Nested n => ContainsKill(n.Body),
        Statement.If i => ContainsKill(i.Accept) || ContainsKill(i.Reject),
        Statement.Loop l => ContainsKill(l.Body) || ContainsKill(l.Continuing),
        Statement.Switch sw => sw.Cases.Any(c => ContainsKill(c.Body)), _ => false
    });
    internal static bool NeedsRelocation(Block body) => body.Statements.Any(s => s switch {
        Statement.Loop l => ContainsKill(l.Continuing) || NeedsRelocation(l.Body) || NeedsRelocation(l.Continuing),
        Statement.Nested n => NeedsRelocation(n.Body),
        Statement.If i => NeedsRelocation(i.Accept) || NeedsRelocation(i.Reject),
        Statement.Switch sw => sw.Cases.Any(c => NeedsRelocation(c.Body)), _ => false
    });
}

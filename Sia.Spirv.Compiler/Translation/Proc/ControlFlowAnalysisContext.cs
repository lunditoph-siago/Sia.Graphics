using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

[Flags]
internal enum ControlFlowAnalyses { None = 0, Predecessors = 1, Dominance = 2, All = Predecessors | Dominance }

/// <summary>One owned graph's lazy analyses. Passes explicitly invalidate results;
/// predecessor entries retain edge identity, whereas dominance retains block IDs.</summary>
internal sealed class ControlFlowAnalysisContext(ControlFlowFunction function)
{
    private IReadOnlyDictionary<int, IReadOnlyList<(ControlFlowBlock Block, ControlFlowEdge Edge)>>? predecessors;
    private IReadOnlyDictionary<int, IReadOnlySet<int>>? dominance;

    internal void RequireFunction(ControlFlowFunction candidate)
    {
        if (!ReferenceEquals(function, candidate)) throw new ArgumentException("Analysis context belongs to another function.", nameof(candidate));
    }

    internal IReadOnlyDictionary<int, IReadOnlyList<(ControlFlowBlock Block, ControlFlowEdge Edge)>> Predecessors
        => predecessors ??= ControlFlowAnalysis.Predecessors(function).ToFrozenDictionary(p => p.Key,
            p => (IReadOnlyList<(ControlFlowBlock Block, ControlFlowEdge Edge)>)p.Value.AsReadOnly());

    internal IReadOnlyDictionary<int, IReadOnlySet<int>> Dominance
        => dominance ??= ControlFlowAnalysis.Dominators(function, Predecessors).ToFrozenDictionary(p => p.Key,
            p => (IReadOnlySet<int>)p.Value.ToFrozenSet());

    internal void Preserve(ControlFlowAnalyses preserved)
    {
        if ((preserved & ControlFlowAnalyses.Predecessors) == 0) predecessors = null;
        if ((preserved & ControlFlowAnalyses.Dominance) == 0) dominance = null;
    }
}

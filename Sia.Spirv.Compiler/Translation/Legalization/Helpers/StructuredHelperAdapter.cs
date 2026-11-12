using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Expand helper calls across deferred bodies and return any readable callers to canonical IR.</summary>
internal static class StructuredHelperAdapter
{
    internal static CanonicalModule Prepare(CanonicalModule canonical, IReadOnlySet<string> selected)
    {
        var module = canonical.Declarations;
        var graphs = canonical.Functions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var deferred = canonical.DeferredFunctions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        CanonicalModule Current() => new(module, graphs, deferred, canonical.EntryFunctions);
        IEnumerable<string> Calls(ShaderFunction function) => ControlFlowAnalysis.Calls(function, graphs);

        // Summaries come from owned graphs; declaration bodies are read only for deferrals.
        var effects = ShaderEffectAnalysis.Compute(Current());
        foreach (var function in module.Functions.Where(f => !graphs.ContainsKey(f.Name)
            && (selected.Contains(f.Name) || Calls(f).Any(selected.Contains))).ToArray())
            Import(function, effects);

        var unsupported = selected.Where(n => !graphs.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        var adapterHelpers = new HashSet<string>(unsupported, StringComparer.Ordinal);
        foreach (var function in module.Functions.Where(f => !graphs.ContainsKey(f.Name)))
            adapterHelpers.UnionWith(Calls(function).Where(selected.Contains));

        // A selected helper that calls an unreadable helper must expand with it.
        bool changed;
        do {
            changed = false;
            foreach (var function in module.Functions.Where(f => selected.Contains(f.Name)))
                if (Calls(function).Any(unsupported.Contains)) changed |= unsupported.Add(function.Name);
        } while (changed);
        adapterHelpers.UnionWith(unsupported);

        var callers = module.Functions.Where(f => !selected.Contains(f.Name)
            && Calls(f).Any(n => unsupported.Contains(n) || !graphs.ContainsKey(f.Name) && adapterHelpers.Contains(n)))
            .ToArray();
        if (callers.Length == 0) return Current();

        foreach (var caller in callers)
            adapterHelpers.UnionWith(Calls(caller).Where(selected.Contains));
        do {
            changed = false;
            foreach (var function in module.Functions.Where(f => adapterHelpers.Contains(f.Name)))
                foreach (string name in Calls(function).Where(selected.Contains)) changed |= adapterHelpers.Add(name);
        } while (changed);

        var callerNames = callers.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var adapter = module.WithFunctions(module.Functions.Select(f =>
            (adapterHelpers.Contains(f.Name) || callerNames.Contains(f.Name)) && graphs.TryGetValue(f.Name, out var graph)
                ? StructuredControlFlowLowering.Project(graph, module) : f));
        var adapted = StructuredHelperInliner.RunSelected(adapter, adapterHelpers, callerNames)
            .Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        module = module.WithFunctions(module.Functions.Select(f => callerNames.Contains(f.Name) ? adapted[f.Name] : f));
        foreach (var caller in callers) {
            graphs.Remove(caller.Name);
            deferred[caller.Name] = "explicit helper adapter";
        }

        effects = ShaderEffectAnalysis.Compute(Current());
        foreach (var caller in callers) Import(adapted[caller.Name], effects);
        return Current();

        void Import(ShaderFunction function, IReadOnlyDictionary<string, ShaderEffects> calleeEffects)
        {
            if (!StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason, calleeEffects, native: true)) {
                deferred[function.Name] = reason!;
                return;
            }
            ControlFlowAnalysis.RemoveUnreachable(graph!);
            ControlFlowVerifier.Validate(graph!, module, calleeEffects: calleeEffects);
            LocalValuePromotion.Run(graph!);
            ControlFlowVerifier.Validate(graph!, module, calleeEffects: calleeEffects);
            graphs.Add(function.Name, graph!);
            deferred.Remove(function.Name);
        }
    }
}

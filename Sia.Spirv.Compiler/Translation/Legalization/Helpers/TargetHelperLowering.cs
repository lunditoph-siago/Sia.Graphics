using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Select and expand helpers whose calls cannot survive target lowering.</summary>
internal static class TargetHelperLowering
{
    internal static CanonicalModule Run(CanonicalModule canonical, bool taskDispatch = false,
        IReadOnlySet<string>? additionalHelpers = null)
    {
        ModuleValidator.Validate(canonical, native: true);

        var entryFunctions = canonical.EntryFunctions;
        var module = canonical.Declarations;
        var graphs = canonical.Functions;
        var helpers = module.Functions.Where(f => f.Stage is null).ToArray();
        IEnumerable<string> Calls(ShaderFunction function) => ControlFlowAnalysis.Calls(function, graphs);
        var called = module.Functions.SelectMany(Calls).ToHashSet(StringComparer.Ordinal);

        var taskHelpers = taskDispatch
            ? helpers.Where(f => graphs.TryGetValue(f.Name, out var graph)
                ? graph.Blocks.Any(b => b.Terminator is ControlFlowTerminator.TaskDispatch)
                : Dispatches(f.Body)).Select(f => f.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        IncludeCallers(taskHelpers);

        var privateHelpers = graphs.Where(p => p.Value.Signature.Stage is null
            && p.Value.Blocks.SelectMany(b => b.Instructions).Any(i =>
                i.Operation is ValueOperation.Symbol
                && i.Result?.Type is ShaderType.Pointer { Space: AddressSpace.Private, Base: ShaderType.Pointer }))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        IncludeCallers(privateHelpers);

        // Native image atomics must bind the actual resource before the target
        // chooses its access qualifier.
        bool nativeImageAtomics = graphs.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions)
            .Any(i => i.Operation is ValueOperation.Builtin { AtomicMemory: not null } call
                && call.Function.StartsWith("textureAtomic", StringComparison.Ordinal));
        bool ImageAtomicHelper(ShaderFunction f) => nativeImageAtomics && f.Arguments.Any(a => CanonicalTypes.Resource(a.Type));
        bool ReturnsReference(ShaderFunction f) => f.ReturnType is ShaderType.Pointer || CanonicalTypes.Resource(f.ReturnType);
        bool HasResourceMerge(ShaderFunction f) => graphs.TryGetValue(f.Name, out var graph)
            && graph.Blocks.Any(b => b.Parameters.Any(p => CanonicalTypes.Resource(p.Type)));
        bool RequiresExpansion(ShaderFunction f) => additionalHelpers?.Contains(f.Name) == true
            || privateHelpers.Contains(f.Name) || taskHelpers.Contains(f.Name) || ReturnsReference(f)
            || ImageAtomicHelper(f) || f.Arguments.Any(a => a.Type is ShaderType.Pointer) || HasResourceMerge(f);
        bool UnusedTargetHelper(ShaderFunction f) => !called.Contains(f.Name)
            && (taskHelpers.Contains(f.Name) || ImageAtomicHelper(f)
                || f.Arguments.Any(a => a.Type is ShaderType.Pointer p
                    && (p.Space != AddressSpace.Function || p.Base is ShaderType.RayQuery)));
        bool CanRemove(ShaderFunction f) => UnusedTargetHelper(f)
            || ReturnsReference(f) && entryFunctions.Contains(f.Name);

        var selected = helpers.Where(f => called.Contains(f.Name) && RequiresExpansion(f))
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0 && !helpers.Any(CanRemove)) return canonical;

        canonical = StructuredHelperAdapter.Prepare(canonical, selected);
        module = canonical.Declarations;
        graphs = canonical.Functions;
        var effects = ShaderEffectAnalysis.Compute(canonical);
        var graphHelpers = selected.Where(graphs.ContainsKey).ToHashSet(StringComparer.Ordinal);
        graphs = graphs.ToDictionary(p => p.Key, p => selected.Contains(p.Key) ? p.Value
            : CanonicalHelperInliner.Run(p.Value, module, StructuredControlFlowReader.ReadHelperGraph, graphs,
                expandFunctions: graphHelpers, calleeEffects: effects), StringComparer.Ordinal);

        var remainingCalls = module.Functions.Where(f => !selected.Contains(f.Name)).SelectMany(Calls)
            .ToHashSet(StringComparer.Ordinal);
        var removed = module.Functions.Where(f => f.Stage is null && !remainingCalls.Contains(f.Name)
            && (selected.Contains(f.Name) || CanRemove(f))).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var output = module.WithFunctions(module.Functions.Where(f => !removed.Contains(f.Name)));
        var result = new CanonicalModule(output,
            graphs.Where(p => !removed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            canonical.DeferredFunctions.Where(p => !removed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            entryFunctions.Where(n => !removed.Contains(n)).ToHashSet(StringComparer.Ordinal));
        ModuleValidator.Validate(result, native: true);
        return result;

        void IncludeCallers(HashSet<string> affected)
        {
            if (affected.Count == 0) return;
            bool changed;
            do {
                changed = false;
                foreach (var helper in helpers)
                    if (Calls(helper).Any(affected.Contains)) changed |= affected.Add(helper.Name);
            } while (changed);
        }
    }

    private static bool Dispatches(Block body) => body.Statements.Any(s => s switch {
        Statement.TaskDispatch => true,
        Statement.Nested n => Dispatches(n.Body),
        Statement.If i => Dispatches(i.Accept) || Dispatches(i.Reject),
        Statement.Loop l => Dispatches(l.Body) || Dispatches(l.Continuing),
        Statement.Switch choice => choice.Cases.Any(c => Dispatches(c.Body)),
        _ => false
    });
}

using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation;

internal sealed record CanonicalPassTrace(string Function, string Pass, string Before, string After,
    string PreservedAnalyses, string InvalidatedAnalyses)
{
    public ControlFlowAnalyses Preserved { get; init; }
    public ControlFlowAnalyses Invalidated { get; init; }
}
internal sealed record CanonicalDeferral(string Function, string Feature);

/// <summary>Incremental common middle-end. Unmigrated feature families retain their existing route explicitly.</summary>
internal static class CanonicalShaderPipeline
{
    public static Module Run(Module input, ICollection<CanonicalPassTrace>? traces = null, ICollection<CanonicalDeferral>? deferrals = null,
        Action<ControlFlowFunction, Module>? verifyFrontend = null,
        IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs = null)
        => StructuredControlFlowLowering.Run(Prepare(input, traces, deferrals, verifyFrontend, frontendGraphs),
            nativeValidation: verifyFrontend is not null);

    public static CanonicalModule Prepare(Module input, ICollection<CanonicalPassTrace>? traces = null,
        ICollection<CanonicalDeferral>? deferrals = null, Action<ControlFlowFunction, Module>? verifyFrontend = null,
        IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs = null)
    {
        if (verifyFrontend is null) ModuleValidator.Validate(input);
        else if (frontendGraphs is not null) ModuleValidator.ValidateNative(input, frontendGraphs);
        else ModuleValidator.ValidateNative(input);
        var entryFunctions = ControlFlowAnalysis.EntryFunctions(input, frontendGraphs);
        var functions = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        var deferredFunctions = new Dictionary<string, string>(StringComparer.Ordinal);
        void Defer(string function, string reason) {
            deferredFunctions.Add(function, reason); deferrals?.Add(new(function, reason));
        }
        foreach (var function in input.Functions) {
            if (!entryFunctions.Contains(function.Name) && frontendGraphs?.ContainsKey(function.Name) != true) {
                Defer(function.Name, "outside shader entry call graph"); continue;
            }
            ControlFlowFunction? graph = null; string? deferred = null;
            bool native = frontendGraphs?.TryGetValue(function.Name, out graph) == true;
            if (!native && !StructuredControlFlowReader.TryRead(function, input, out graph, out deferred)) {
                Defer(function.Name, deferred!); continue;
            }
            // Native frontend graphs are borrowed just like declarations. Shared
            // passes own their working copies, including edge argument lists.
            if (native) graph = graph!.Copy();
            var analyses = new ControlFlowAnalysisContext(graph!);
            string? before = traces is null ? null : ControlFlowPrinter.Write(graph!);
            if (native) traces?.Add(new(function.Name, "native-cfg-import", before!, before!,
                "native block topology, phi arguments and source evaluation order", "none") { Preserved = ControlFlowAnalyses.All });
            ControlFlowAnalysis.RemoveUnreachable(graph!);
            analyses.Preserve(ControlFlowAnalyses.None);
            ControlFlowVerifier.Validate(graph!, input, analyses);
            traces?.Add(new(function.Name, "remove-unreachable", before!, ControlFlowPrinter.Write(graph!),
                "reachable effects and values", "predecessors, dominance") { Invalidated = ControlFlowAnalyses.All });
            before = traces is null ? null : ControlFlowPrinter.Write(graph!);
            var expanded = CanonicalHelperInliner.Run(graph!, input, frontendGraphs);
            if (!ReferenceEquals(graph, expanded)) {
                analyses = new ControlFlowAnalysisContext(expanded);
                traces?.Add(new(function.Name, "pointer-return-helper-expansion", before!, ControlFlowPrinter.Write(expanded),
                    "argument evaluation order, address identity, callee effect order", "CFG topology, predecessors, dominance, call summaries") {
                    Invalidated = ControlFlowAnalyses.All
                });
            }
            graph = expanded;
            ControlFlowVerifier.Validate(graph, input, analyses);
            // Frontend-specific invariants must be checked while addresses are still
            // SSA values, before target address dispatch erases those native facts.
            verifyFrontend?.Invoke(graph!, input);
            before = traces is null ? null : ControlFlowPrinter.Write(graph);
            LocalValuePromotion.Run(graph, analyses);
            traces?.Add(new(function.Name, "local-value-promotion", before!, ControlFlowPrinter.Write(graph),
                "CFG topology, predecessor edges, block dominance, external memory effects", "value definitions and value dominance") {
                Preserved = ControlFlowAnalyses.All
            });
            ControlFlowVerifier.Validate(graph, input, analyses);
            functions.Add(function.Name, graph);
        }
        var result = new CanonicalModule(input, functions.ToFrozenDictionary(StringComparer.Ordinal),
            deferredFunctions.ToFrozenDictionary(StringComparer.Ordinal), entryFunctions.ToFrozenSet(StringComparer.Ordinal));
        var effects = ShaderEffectAnalysis.Compute(result);
        foreach (var graph in functions.Values) {
            string? before = traces is null ? null : ControlFlowPrinter.Write(graph);
            foreach (var block in graph.Blocks)
                for (int i = 0; i < block.Instructions.Count; i++)
                    if (block.Instructions[i].Operation is ValueOperation.Call call
                        && effects.TryGetValue(call.Function, out var callee) && (block.Instructions[i].Effects & callee) != callee) {
                        block.Instructions[i] = block.Instructions[i] with { Operation = call with { CalleeEffects = call.CalleeEffects | callee } };
                    }
            traces?.Add(new(graph.Signature.Name, "canonical-call-effects", before!, ControlFlowPrinter.Write(graph),
                "CFG topology, predecessor edges, dominance, argument evaluation order", "call requirement summaries") {
                Preserved = ControlFlowAnalyses.All
            });
        }
        ModuleValidator.Validate(result, native: verifyFrontend is not null);
        return result;
    }
}

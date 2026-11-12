using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
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

internal static class CanonicalShaderPipeline
{
    private readonly record struct GraphPass(
        string Name,
        Action<ControlFlowFunction, ControlFlowAnalysisContext> Run,
        string Preserved,
        string Invalidated);

    private static readonly GraphPass[] s_ValuePasses = [
        new("local-value-numbering", static (graph, analyses) => LocalValueNumbering.Run(graph, analyses),
            "CFG topology, dominance, evaluation stages, operand order and ordered effects", "redundant pure definitions"),
        new("constant-conversion-folding", static (graph, _) => ConstantConversionFolding.Run(graph),
            "CFG topology, predecessor edges, dominance, SSA identities and ordered effects", "converted constant values"),
        new("dead-value-elimination", static (graph, analyses) => DeadValueElimination.Run(graph, analyses),
            "CFG topology, dominance and ordered effects", "unused pure definitions and phi arguments")
    ];

    public static CanonicalModule Prepare(Module input)
        => Prepare(new StructuredModuleSource(input));

    public static CanonicalModule Prepare(
        ICanonicalModuleSource source,
        ICollection<CanonicalPassTrace>? traces = null,
        ICollection<CanonicalDeferral>? deferrals = null)
    {
        source.ValidateDeclarations();
        var input = source.Declarations;
        var entryFunctions = ControlFlowAnalysis.EntryFunctions(input, source.NativeGraphs);
        var functions = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        var deferredFunctions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var function in input.Functions) {
            var imported = source.ReadGraph(function);
            if (imported.Graph is not { } graph) {
                var reason = imported.DeferredFeature!;
                deferredFunctions.Add(function.Name, reason);
                deferrals?.Add(new(function.Name, reason));
                continue;
            }
            functions.Add(function.Name, OptimizeGraph(source, function.Name, graph, imported.IsNative, traces));
        }
        var result = new CanonicalModule(input, functions.ToFrozenDictionary(StringComparer.Ordinal),
            deferredFunctions.ToFrozenDictionary(StringComparer.Ordinal), entryFunctions.ToFrozenSet(StringComparer.Ordinal));
        ShaderEffectAnalysis.RefreshCalls(result, traces);
        ModuleValidator.Validate(result, native: source.UsesNativeValidation);
        return result;
    }

    private static ControlFlowFunction OptimizeGraph(
        ICanonicalModuleSource source,
        string function,
        ControlFlowFunction graph,
        bool native,
        ICollection<CanonicalPassTrace>? traces)
    {
        var input = source.Declarations;
        var analyses = new ControlFlowAnalysisContext(graph);
        string? Snapshot() => traces is null ? null : ControlFlowPrinter.Write(graph);
        void Trace(string pass, string? before, string preserved, string invalidated, bool keepsAnalyses)
        {
            traces?.Add(new(function, pass, before!, ControlFlowPrinter.Write(graph), preserved, invalidated) {
                Preserved = keepsAnalyses ? ControlFlowAnalyses.All : ControlFlowAnalyses.None,
                Invalidated = keepsAnalyses ? ControlFlowAnalyses.None : ControlFlowAnalyses.All
            });
        }

        var before = Snapshot();
        if (native) {
            traces?.Add(new(function, "native-cfg-import", before!, before!,
                "native block topology, phi arguments and source evaluation order", "none") { Preserved = ControlFlowAnalyses.All });
        }
        ControlFlowAnalysis.RemoveUnreachable(graph);
        analyses.Preserve(ControlFlowAnalyses.None);
        ControlFlowVerifier.Validate(graph, input, analyses);
        Trace("remove-unreachable", before, "reachable effects and values", "predecessors, dominance", keepsAnalyses: false);

        before = Snapshot();
        var expanded = CanonicalHelperInliner.Run(graph, input, StructuredControlFlowReader.ReadHelperGraph, source.NativeGraphs);
        if (!ReferenceEquals(graph, expanded)) {
            graph = expanded;
            analyses = new ControlFlowAnalysisContext(graph);
            Trace("pointer-return-helper-expansion", before, "argument evaluation order, address identity, callee effect order",
                "CFG topology, predecessors, dominance, call summaries", keepsAnalyses: false);
        }
        ControlFlowVerifier.Validate(graph, input, analyses);
        source.ValidateGraph(graph);

        before = Snapshot();
        AggregateReadPromotion.Run(graph);
        LocalValuePromotion.Run(graph, analyses);
        Trace("local-value-promotion", before, "CFG topology, predecessor edges, block dominance, external memory effects",
            "value definitions and value dominance", keepsAnalyses: true);
        ControlFlowVerifier.Validate(graph, input, analyses);

        foreach (var pass in s_ValuePasses) {
            before = Snapshot();
            pass.Run(graph, analyses);
            ControlFlowVerifier.Validate(graph, input, analyses);
            Trace(pass.Name, before, pass.Preserved, pass.Invalidated, keepsAnalyses: true);
        }
        return graph;
    }
}

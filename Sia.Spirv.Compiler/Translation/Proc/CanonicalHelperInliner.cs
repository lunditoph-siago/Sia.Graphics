using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Expand address-return/slot and selected pointer-argument calls on verified CFG, retaining address identity
/// and joining actual return values rather than copying pointed-to data.</summary>
internal static class CanonicalHelperInliner
{
    internal static CanonicalModule RunReferences(CanonicalModule canonical)
    {
        ModuleValidator.Validate(canonical, native: true);
        var module = canonical.Declarations;
        var graphs = canonical.Functions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var deferred = canonical.DeferredFunctions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        IEnumerable<string> Calls(ShaderFunction function) => graphs.TryGetValue(function.Name, out var graph)
            ? graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>().Select(c => c.Function)
            : ControlFlowAnalysis.Calls(function.Body);
        var called = module.Functions.SelectMany(Calls).ToHashSet(StringComparer.Ordinal);
        var privateHelpers = graphs.Where(p => p.Value.Signature.Stage is null && p.Value.Blocks.SelectMany(b => b.Instructions).Any(i =>
            i.Operation is ValueOperation.Symbol && i.Result?.Type is ShaderType.Pointer { Space: AddressSpace.Private, Base: ShaderType.Pointer }))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        bool privateChanged;
        do {
            privateChanged = false;
            foreach (var function in module.Functions.Where(f => f.Stage is null))
                if (Calls(function).Any(privateHelpers.Contains)) privateChanged |= privateHelpers.Add(function.Name);
        } while (privateChanged);
        var selected = module.Functions.Where(f => f.Stage is null && called.Contains(f.Name)
            && (privateHelpers.Contains(f.Name) || f.ReturnType is ShaderType.Pointer || CanonicalTypes.Resource(f.ReturnType)
                || f.Arguments.Any(a => a.Type is ShaderType.Pointer)
                || graphs.TryGetValue(f.Name, out var resourceGraph)
                    && resourceGraph.Blocks.Any(b => b.Parameters.Any(p => CanonicalTypes.Resource(p.Type)))))
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        bool UnusedTargetHelper(ShaderFunction f) => f.Stage is null && !called.Contains(f.Name)
            && f.Arguments.Any(a => a.Type is ShaderType.Pointer p && (p.Space != AddressSpace.Function || p.Base is ShaderType.RayQuery));
        if (selected.Count == 0 && !module.Functions.Any(f => UnusedTargetHelper(f)
            || f.Stage is null && (f.ReturnType is ShaderType.Pointer || CanonicalTypes.Resource(f.ReturnType)) && canonical.EntryFunctions.Contains(f.Name))) return canonical;

        Module Declarations(IEnumerable<ShaderFunction> functions) {
            var output = new Module { VulkanMemoryModel = module.VulkanMemoryModel, WorkgroupInitializationRequired = module.WorkgroupInitializationRequired };
            output.Structures.AddRange(module.Structures); output.Constants.AddRange(module.Constants); output.Globals.AddRange(module.Globals);
            output.Enables.UnionWith(module.Enables); output.DiagnosticFilters.AddRange(module.DiagnosticFilters); output.Functions.AddRange(functions);
            return output;
        }
        CanonicalModule Current() => new(module, graphs, deferred, canonical.EntryFunctions);
        var effects = ShaderEffectAnalysis.Compute(Current());
        void Import(ShaderFunction function) {
            if (!StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason, effects, native: true)) {
                deferred[function.Name] = reason!; return;
            }
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module, calleeEffects: effects);
            LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, module, calleeEffects: effects);
            graphs.Add(function.Name, graph!); deferred.Remove(function.Name);
        }
        // Only explicitly deferred implementations cross the reader boundary.
        // Summaries come from owned graphs, never obsolete declaration bodies.
        foreach (var function in module.Functions.Where(f => !graphs.ContainsKey(f.Name)
            && (selected.Contains(f.Name) || Calls(f).Any(selected.Contains))).ToArray()) Import(function);

        var unsupported = selected.Where(n => !graphs.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        var adapterHelpers = new HashSet<string>(unsupported, StringComparer.Ordinal);
        foreach (var function in module.Functions.Where(f => !graphs.ContainsKey(f.Name)))
            adapterHelpers.UnionWith(Calls(function).Where(selected.Contains));
        // A pointer helper that calls an unreadable helper must expand with it.
        // Ordinary callers need no adapter merely because they share a readable
        // helper with a deferred caller: they retain the canonical inliner.
        bool changed;
        do {
            changed = false;
            foreach (var function in module.Functions.Where(f => selected.Contains(f.Name)))
                if (Calls(function).Any(unsupported.Contains)) changed |= unsupported.Add(function.Name);
        } while (changed);
        adapterHelpers.UnionWith(unsupported);
        var adaptedCallers = module.Functions.Where(f => !selected.Contains(f.Name)
            && Calls(f).Any(n => unsupported.Contains(n) || !graphs.ContainsKey(f.Name) && adapterHelpers.Contains(n)))
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (adaptedCallers.Count != 0) {
            foreach (var function in module.Functions.Where(f => adaptedCallers.Contains(f.Name)))
                adapterHelpers.UnionWith(Calls(function).Where(selected.Contains));
            do {
                changed = false;
                foreach (var function in module.Functions.Where(f => adapterHelpers.Contains(f.Name)))
                    foreach (string name in Calls(function).Where(selected.Contains)) changed |= adapterHelpers.Add(name);
            } while (changed);
            var adapter = Declarations(module.Functions.Select(f => (adapterHelpers.Contains(f.Name) || adaptedCallers.Contains(f.Name))
                && graphs.TryGetValue(f.Name, out var graph) ? Legalization.ShaderTargetLowering.ForStructured(graph, module) : f));
            var adapted = HelperInliner.RunSelected(adapter, adapterHelpers, adaptedCallers).Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
            module = Declarations(module.Functions.Select(f => adaptedCallers.Contains(f.Name) ? adapted[f.Name] : f));
            foreach (string name in adaptedCallers) { graphs.Remove(name); deferred[name] = "explicit helper adapter"; }
            effects = ShaderEffectAnalysis.Compute(Current());
            foreach (var function in module.Functions.Where(f => adaptedCallers.Contains(f.Name))) Import(function);
        }
        effects = ShaderEffectAnalysis.Compute(Current());
        var graphHelpers = selected.Where(graphs.ContainsKey).ToHashSet(StringComparer.Ordinal);
        graphs = graphs.ToDictionary(p => p.Key,
            p => selected.Contains(p.Key) ? p.Value : Run(p.Value, module, graphs, expandFunctions: graphHelpers, calleeEffects: effects), StringComparer.Ordinal);
        var remainingCalls = module.Functions.Where(f => !selected.Contains(f.Name)).SelectMany(f => graphs.TryGetValue(f.Name, out var graph)
            ? graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>().Select(c => c.Function)
            : ControlFlowAnalysis.Calls(f.Body)).ToHashSet(StringComparer.Ordinal);
        var removed = module.Functions.Where(f => f.Stage is null && !remainingCalls.Contains(f.Name)
            && (selected.Contains(f.Name) || UnusedTargetHelper(f) || (f.ReturnType is ShaderType.Pointer || CanonicalTypes.Resource(f.ReturnType)) && canonical.EntryFunctions.Contains(f.Name)))
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var output = Declarations(module.Functions.Where(f => !removed.Contains(f.Name)));
        var result = new CanonicalModule(output, graphs.Where(p => !removed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            deferred.Where(p => !removed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            canonical.EntryFunctions.Where(n => !removed.Contains(n)).ToHashSet(StringComparer.Ordinal));
        ModuleValidator.Validate(result, native: true); return result;
    }

    public static ControlFlowFunction Run(ControlFlowFunction input, Module module,
        IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs = null, bool expandPointerSlots = false,
        IReadOnlySet<string>? expandFunctions = null, IReadOnlyDictionary<string, ShaderEffects>? calleeEffects = null)
    {
        bool calls = input.Blocks.SelectMany(b => b.Instructions).Any(i => RequiresExpansion(i.Operation, expandPointerSlots, expandFunctions));
        if (!calls && input.Signature.ReturnType is not ShaderType.Pointer) return input;
        calleeEffects ??= ShaderEffectAnalysis.Compute(module, frontendGraphs);
        ControlFlowVerifier.Validate(input, module, calleeEffects: calleeEffects);
        if (input.Signature.ReturnType is ShaderType.Pointer) CheckLifetime(input, module);
        return Expand(input, module, new(StringComparer.Ordinal), frontendGraphs, expandPointerSlots, expandFunctions, calleeEffects);
    }

    private static bool RequiresExpansion(ValueOperation operation, bool slots, IReadOnlySet<string>? functions) => operation is ValueOperation.Call call
        && (call.ReturnType is ShaderType.Pointer || CanonicalTypes.Resource(call.ReturnType) || functions?.Contains(call.Function) == true
            || slots && call.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.Pointer }));

    private static ControlFlowFunction Expand(ControlFlowFunction input, Module module, HashSet<string> active,
        IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs, bool expandPointerSlots, IReadOnlySet<string>? expandFunctions,
        IReadOnlyDictionary<string, ShaderEffects> calleeEffects)
    {
        if (!input.Blocks.SelectMany(b => b.Instructions).Any(i => RequiresExpansion(i.Operation, expandPointerSlots, expandFunctions))) return input;
        if (!active.Add(input.Signature.Name)) throw Error("recursive pointer-return helper", input.Signature.Name);
        // Keep the caller's SSA identities, including each call result. Callee
        // values alone receive fresh IDs from this compilation-owned copy.
        var output = input.Copy();
        while (output.Blocks.SelectMany(b => b.Instructions.Select((i, index) => (Block: b, Instruction: i, Index: index)))
            .FirstOrDefault(p => RequiresExpansion(p.Instruction.Operation, expandPointerSlots, expandFunctions)) is { Instruction: not null } site) {
            var call = (ValueOperation.Call)site.Instruction.Operation;
            var callee = module.Functions.Single(f => f.Name == call.Function);
            ControlFlowFunction? graph = null; string? reason = null;
            if (frontendGraphs?.TryGetValue(callee.Name, out graph) != true
                && !StructuredControlFlowReader.TryRead(callee, module, out graph, out reason, calleeEffects))
                throw Error("pointer-return helper cannot enter canonical IR: " + reason, callee.Name);
            graph = graph!.Copy();
            ControlFlowAnalysis.RemoveUnreachable(graph); ControlFlowVerifier.Validate(graph, module, calleeEffects: calleeEffects);
            graph = Expand(graph, module, active, frontendGraphs, expandPointerSlots, expandFunctions, calleeEffects);
            if (graph.Signature.ReturnType is ShaderType.Pointer) CheckLifetime(graph, module);
            var continuation = output.Block();
            if (site.Instruction.Result is { } result) continuation.Parameters.Add(result);
            continuation.Instructions.AddRange(site.Block.Instructions.Skip(site.Index + 1)); continuation.Terminator = site.Block.Terminator;
            site.Block.Instructions.RemoveRange(site.Index, site.Block.Instructions.Count - site.Index);
            if (output.SelectionMerges.Remove(site.Block.Id, out int? merge)) output.SelectionMerges.Add(continuation.Id, merge);
            else if (output.Loops.ContainsKey(site.Block.Id) && continuation.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch)
                // The moved loop exit stays inside the loop body. Its merge is
                // owned by the loop, not by an additional nested selection.
                output.SelectionMerges.Add(continuation.Id, null);
            var arguments = callee.Arguments.Select((a, i) => (a.Name, Value: call.Arguments[i])).ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);
            int entry = Copy(graph, output, arguments, continuation, site.Instruction.DiagnosticFilters, module);
            output.DiagnosticRanges.AddRange(graph.DiagnosticRanges);
            if (graph.BodyDiagnosticFilters.Count != 0) output.DiagnosticRanges.Add(graph.BodyDiagnosticFilters);
            // A single-arm selection provides the helper's lexical exit target without
            // introducing a loop or changing convergence. Arguments are already SSA values.
            var selector = output.Value(ShaderType.U32); site.Block.Instructions.Add(new(selector, new ValueOperation.Literal(0u), site.Instruction.Span) {
                // An empty callee has no copied instruction carrying its lexical
                // settings. Retain that scope on the synthetic call-site value.
                DiagnosticFilters = graph.Signature.DiagnosticFilters.Concat(site.Instruction.DiagnosticFilters)
                    .DistinctBy(f => (f.Namespace, f.Rule)).ToArray()
            });
            site.Block.Terminator = new ControlFlowTerminator.Switch(selector, [], new(entry));
            output.SelectionMerges.Add(site.Block.Id, continuation.Id);
        }
        active.Remove(input.Signature.Name);
        ControlFlowAnalysis.RemoveUnreachable(output); ControlFlowVerifier.Validate(output, module, calleeEffects: calleeEffects); return output;
    }

    private static ShaderException Error(string message, string function)
        => new(DiagnosticStage.Validation, "Canonical helper in " + function + ": " + message);

    private static void CheckLifetime(ControlFlowFunction graph, Module module)
    {
        var origins = PointerAliasAnalysis.Origins(module, graph);
        foreach (var returned in graph.Blocks.Select(b => b.Terminator).OfType<ControlFlowTerminator.Return>())
            if (returned.Value is { Type: ShaderType.Pointer } address && origins[address.Id].Any(r => r.Local != 0))
                throw Error("callee-local returned address requires lifetime legalization", graph.Signature.Name);
    }

    private static int Copy(ControlFlowFunction source, ControlFlowFunction target,
        IReadOnlyDictionary<string, SsaValue>? arguments, ControlFlowBlock? continuation,
        IReadOnlyList<DiagnosticFilter>? callerFilters, Module module)
    {
        var blocks = source.Blocks.ToDictionary(b => b.Id, _ => target.Block());
        var values = new Dictionary<int, SsaValue>();
        foreach (var block in source.Blocks) {
            foreach (var parameter in block.Parameters) { var value = target.Value(parameter.Type); values.Add(parameter.Id, value); blocks[block.Id].Parameters.Add(value); }
            foreach (var instruction in block.Instructions.Where(i => i.Result is not null)) {
                var result = instruction.Result!.Value;
                values.Add(result.Id, instruction.Operation is ValueOperation.Symbol symbol && arguments?.TryGetValue(symbol.Name, out var argument) == true
                    ? argument : target.Value(result.Type));
            }
        }
        SsaValue Value(SsaValue value) => values[value.Id];
        ControlFlowEdge Edge(ControlFlowEdge edge) => new(blocks[edge.Target].Id, edge.Arguments.Select(Value));
        var defaults = new[] { new DiagnosticFilter(DiagnosticSeverity.Error, "derivative_uniformity"), new DiagnosticFilter(DiagnosticSeverity.Error, "subgroup_uniformity") };
        DiagnosticSeverity CallerSeverity(DiagnosticFilter filter) => callerFilters!.Concat(target.Signature.DiagnosticFilters)
            .Concat(module.DiagnosticFilters).FirstOrDefault(f => f.Namespace == filter.Namespace && f.Rule == filter.Rule)?.Severity ?? DiagnosticSeverity.Error;
        foreach (var block in source.Blocks) {
            var copy = blocks[block.Id];
            foreach (var instruction in block.Instructions) {
                if (instruction.Operation is ValueOperation.Symbol symbol && arguments?.ContainsKey(symbol.Name) == true) continue;
                var explicitFilters = instruction.DiagnosticFilters.Concat(source.Signature.DiagnosticFilters).ToArray();
                // Keep lexical callee settings, and restore inherited/default rules only
                // where they differ from the caller. Equal defaults need no block attribute.
                var filters = callerFilters is null ? instruction.DiagnosticFilters : explicitFilters
                    .Concat(module.DiagnosticFilters).Concat(defaults).DistinctBy(f => (f.Namespace, f.Rule))
                    .Where(f => CallerSeverity(f) != f.Severity || explicitFilters.Any(e => e.Namespace == f.Namespace && e.Rule == f.Rule)).ToArray();
                copy.Instructions.Add(instruction with { Result = instruction.Result is { } result ? Value(result) : null,
                    Operation = instruction.Operation.Map(Value), DiagnosticFilters = filters });
            }
            copy.Terminator = block.Terminator switch {
                ControlFlowTerminator.Branch b => new ControlFlowTerminator.Branch(Edge(b.Edge)),
                ControlFlowTerminator.Conditional c => new ControlFlowTerminator.Conditional(Value(c.Condition), Edge(c.Accept), Edge(c.Reject)),
                ControlFlowTerminator.Switch s => new ControlFlowTerminator.Switch(Value(s.Selector), s.Cases.Select(c => new ControlFlowCase(c.Values, Edge(c.Edge))).ToArray(), Edge(s.Default)),
                ControlFlowTerminator.Return r when continuation is not null => new ControlFlowTerminator.Branch(new(continuation.Id, r.Value is { } v ? [Value(v)] : [])),
                ControlFlowTerminator.Return r => r with { Value = r.Value is { } v ? Value(v) : null },
                ControlFlowTerminator.Unreachable u => u,
                ControlFlowTerminator.InvocationKill k => k,
                ControlFlowTerminator.TaskDispatch d => d with { Dimensions = Value(d.Dimensions) },
                _ => throw Error("missing callee terminator", source.Signature.Name)
            };
        }
        foreach (var (header, merge) in source.SelectionMerges) target.SelectionMerges.Add(blocks[header].Id, merge is int id ? blocks[id].Id : null);
        foreach (var (header, loop) in source.Loops) target.Loops.Add(blocks[header].Id,
            new(loop.Continuing is int continuing ? blocks[continuing].Id : null, loop.Merge is int merge ? blocks[merge].Id : null));
        return blocks[source.Entry].Id;
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static class CanonicalHelperInliner
{
    internal delegate bool ReadHelperGraph(ShaderFunction function, Module module,
        out ControlFlowFunction? graph, out string? reason, IReadOnlyDictionary<string, ShaderEffects> effects);

    internal static ControlFlowFunction Run(ControlFlowFunction input, Module module, ReadHelperGraph readHelper,
        IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs = null, bool expandPointerSlots = false,
        IReadOnlySet<string>? expandFunctions = null, IReadOnlyDictionary<string, ShaderEffects>? calleeEffects = null)
    {
        bool calls = input.Blocks.SelectMany(b => b.Instructions).Any(i => RequiresExpansion(i.Operation, expandPointerSlots, expandFunctions));
        if (!calls && input.Signature.ReturnType is not ShaderType.Pointer) return input;
        calleeEffects ??= ShaderEffectAnalysis.Compute(module, frontendGraphs);
        ControlFlowVerifier.Validate(input, module, calleeEffects: calleeEffects);
        if (input.Signature.ReturnType is ShaderType.Pointer) CheckLifetime(input, module);
        return Expand(input, module, readHelper, new(StringComparer.Ordinal), frontendGraphs, expandPointerSlots, expandFunctions, calleeEffects);
    }

    private static bool RequiresExpansion(ValueOperation operation, bool slots, IReadOnlySet<string>? functions) => operation is ValueOperation.Call call
        && (call.ReturnType is ShaderType.Pointer || CanonicalTypes.Resource(call.ReturnType) || functions?.Contains(call.Function) == true
            || slots && call.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.Pointer }));

    private static ControlFlowFunction Expand(ControlFlowFunction input, Module module, ReadHelperGraph readHelper, HashSet<string> active,
        IReadOnlyDictionary<string, ControlFlowFunction>? frontendGraphs, bool expandPointerSlots, IReadOnlySet<string>? expandFunctions,
        IReadOnlyDictionary<string, ShaderEffects> calleeEffects)
    {
        if (!input.Blocks.SelectMany(b => b.Instructions).Any(i => RequiresExpansion(i.Operation, expandPointerSlots, expandFunctions))) return input;
        if (!active.Add(input.Signature.Name)) throw Error("recursive pointer-return helper", input.Signature.Name);
        var output = input.Copy();
        while (output.Blocks.SelectMany(b => b.Instructions.Select((i, index) => (Block: b, Instruction: i, Index: index)))
            .FirstOrDefault(p => RequiresExpansion(p.Instruction.Operation, expandPointerSlots, expandFunctions)) is { Instruction: not null } site) {
            var call = (ValueOperation.Call)site.Instruction.Operation;
            var callee = module.Functions.Single(f => f.Name == call.Function);
            ControlFlowFunction? graph = null; string? reason = null;
            if (frontendGraphs?.TryGetValue(callee.Name, out graph) != true
                && !readHelper(callee, module, out graph, out reason, calleeEffects))
                throw Error("pointer-return helper cannot enter canonical IR: " + reason, callee.Name);
            graph = graph!.Copy();
            ControlFlowAnalysis.RemoveUnreachable(graph); ControlFlowVerifier.Validate(graph, module, calleeEffects: calleeEffects);
            graph = Expand(graph, module, readHelper, active, frontendGraphs, expandPointerSlots, expandFunctions, calleeEffects);
            if (graph.Signature.ReturnType is ShaderType.Pointer) CheckLifetime(graph, module);
            var continuation = output.Block();
            if (site.Instruction.Result is { } result) continuation.Parameters.Add(result);
            continuation.Instructions.AddRange(site.Block.Instructions.Skip(site.Index + 1)); continuation.Terminator = site.Block.Terminator;
            site.Block.Instructions.RemoveRange(site.Index, site.Block.Instructions.Count - site.Index);
            if (output.SelectionMerges.Remove(site.Block.Id, out int? merge)) output.SelectionMerges.Add(continuation.Id, merge);
            else if (output.Loops.ContainsKey(site.Block.Id) && continuation.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch)
                output.SelectionMerges.Add(continuation.Id, null);
            var arguments = callee.Arguments.Select((a, i) => (a.Name, Value: call.Arguments[i])).ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);
            int entry = CopyCallee(graph, output, arguments, continuation, site.Instruction.DiagnosticFilters, module);
            output.DiagnosticRanges.AddRange(graph.DiagnosticRanges);
            if (graph.BodyDiagnosticFilters.Count != 0) output.DiagnosticRanges.Add(graph.BodyDiagnosticFilters);
            var selector = output.Value(ShaderType.U32); site.Block.Instructions.Add(new(selector, new ValueOperation.Literal(0u), site.Instruction.Span) {
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

    private static int CopyCallee(ControlFlowFunction source, ControlFlowFunction target,
        IReadOnlyDictionary<string, SsaValue> arguments, ControlFlowBlock continuation,
        IReadOnlyList<DiagnosticFilter> callerFilters, Module module)
    {
        var blocks = source.Blocks.ToDictionary(b => b.Id, _ => target.Block());
        var values = new Dictionary<int, SsaValue>();
        foreach (var block in source.Blocks) {
            foreach (var parameter in block.Parameters) { var value = target.Value(parameter.Type); values.Add(parameter.Id, value); blocks[block.Id].Parameters.Add(value); }
            foreach (var instruction in block.Instructions.Where(i => i.Result is not null)) {
                var result = instruction.Result!.Value;
                values.Add(result.Id, instruction.Operation is ValueOperation.Symbol symbol && arguments.TryGetValue(symbol.Name, out var argument)
                    ? argument : target.Value(result.Type));
            }
        }
        SsaValue Value(SsaValue value) => values[value.Id];
        ControlFlowEdge Edge(ControlFlowEdge edge) => new(blocks[edge.Target].Id, edge.Arguments.Select(Value));
        var defaults = new[] { new DiagnosticFilter(DiagnosticSeverity.Error, "derivative_uniformity"), new DiagnosticFilter(DiagnosticSeverity.Error, "subgroup_uniformity") };
        DiagnosticSeverity CallerSeverity(DiagnosticFilter filter) => callerFilters.Concat(target.Signature.DiagnosticFilters)
            .Concat(module.DiagnosticFilters).FirstOrDefault(f => f.Namespace == filter.Namespace && f.Rule == filter.Rule)?.Severity ?? DiagnosticSeverity.Error;
        foreach (var block in source.Blocks) {
            var copy = blocks[block.Id];
            foreach (var instruction in block.Instructions) {
                if (instruction.Operation is ValueOperation.Symbol symbol && arguments.ContainsKey(symbol.Name)) continue;
                var explicitFilters = instruction.DiagnosticFilters.Concat(source.Signature.DiagnosticFilters).ToArray();
                var filters = explicitFilters
                    .Concat(module.DiagnosticFilters).Concat(defaults).DistinctBy(f => (f.Namespace, f.Rule))
                    .Where(f => CallerSeverity(f) != f.Severity || explicitFilters.Any(e => e.Namespace == f.Namespace && e.Rule == f.Rule)).ToArray();
                copy.Instructions.Add(instruction with {
                    Result = instruction.Result is { } result ? Value(result) : null,
                    Operation = instruction.Operation.Map(Value),
                    DiagnosticFilters = filters
                });
            }
            copy.Terminator = block.Terminator switch {
                ControlFlowTerminator.Branch b => new ControlFlowTerminator.Branch(Edge(b.Edge)),
                ControlFlowTerminator.Conditional c => new ControlFlowTerminator.Conditional(Value(c.Condition), Edge(c.Accept), Edge(c.Reject)),
                ControlFlowTerminator.Switch s => new ControlFlowTerminator.Switch(Value(s.Selector), s.Cases.Select(c => new ControlFlowCase(c.Values, Edge(c.Edge))).ToArray(), Edge(s.Default)),
                ControlFlowTerminator.Return r => new ControlFlowTerminator.Branch(new(continuation.Id, r.Value is { } v ? [Value(v)] : [])),
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

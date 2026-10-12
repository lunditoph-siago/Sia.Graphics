using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// The canonical graph owns all executable edges. Unreachable structural targets
// are target-only labels: they carry no SSA values and serialize as OpUnreachable.
internal sealed record SpirvFunctionControlFlow(ControlFlowFunction Graph, IReadOnlyList<int> BlockOrder,
    IReadOnlyDictionary<int, int> SelectionMerges, IReadOnlyDictionary<int, (int Continuing, int Merge)> Loops,
    IReadOnlySet<int> StructuralTargets, IReadOnlyDictionary<int, int> StructuralBackedges,
    IReadOnlyDictionary<int, SpirvRuntimeArrayLength> RuntimeArrayLengths);

internal sealed record SpirvRuntimeArrayLength(SsaValue? Structure, string? Global, uint Member);

internal static class SpirvControlFlowLowering
{
    internal static SpirvPhysicalLayout Prepare(SpirvPhysicalLayout layout)
    {
        var functions = new Dictionary<string, SpirvFunctionControlFlow>(StringComparer.Ordinal);
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        var deferred = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var function in layout.Module.Functions) {
            if (!StructuredControlFlowReader.TryRead(function, layout.Module, out var graph, out var reason)) {
                deferred.Add(function.Name, reason!); continue;
            }
            ControlFlowAnalysis.RemoveUnreachable(graph!);
            if (graph!.Blocks.SelectMany(b => b.Parameters).Any(v => v.Type is ShaderType.Pointer)) {
                deferred.Add(function.Name, "target pointer merge legalization"); continue;
            }
            ControlFlowVerifier.Validate(graph!, layout.Module);
            // High-level ray-query builtins still expand guards inside the legacy
            // backend. They require their own target lowering before CFG emission.
            if (graph!.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Builtin b
                && (b.Function.StartsWith("rayQuery", StringComparison.Ordinal)
                    || b.Function is "getCommittedHitVertexPositions" or "getCandidateHitVertexPositions"))) {
                deferred.Add(function.Name, "ray-query guard legalization"); continue;
            }
            LocalValuePromotion.Run(graph);
            ControlFlowVerifier.Validate(graph, layout.Module);
            if (graph.Blocks.SelectMany(b => b.Parameters).Any(v => v.Type is ShaderType.Pointer)) {
                deferred.Add(function.Name, "target pointer merge legalization"); continue;
            }
            SpirvMemoryAccessLowering.Run(graph, layout.Module);
            graphs.Add(function.Name, graph);
        }
        // Legacy qualification is confined to explicit deferrals. Graph-owned
        // executable bodies are never rebuilt just to publish target metadata.
        var module = SpirvMemoryAccessLowering.Run(layout.Module, deferred.Keys.ToHashSet(StringComparer.Ordinal));
        var canonical = new CanonicalModule(module, graphs, deferred, new HashSet<string>(StringComparer.Ordinal));
        var effects = ShaderEffectAnalysis.Compute(canonical);
        foreach (var graph in graphs.Values) {
            foreach (var block in graph.Blocks)
                for (int i = 0; i < block.Instructions.Count; i++)
                    if (block.Instructions[i].Operation is ValueOperation.Call call)
                        block.Instructions[i] = block.Instructions[i] with { Operation = call with { CalleeEffects = call.CalleeEffects | effects[call.Function] } };
            functions.Add(graph.Signature.Name, Prepare(graph, module, effects));
        }
        ControlFlowVerifier.Validate(canonical);
        return layout with { Module = module, ControlFlow = functions.ToFrozenDictionary(StringComparer.Ordinal),
            DeferredControlFlow = deferred.ToFrozenDictionary(StringComparer.Ordinal) };
    }

    internal static SpirvFunctionControlFlow Prepare(ControlFlowFunction input, Module module,
        IReadOnlyDictionary<string, ShaderEffects>? calleeEffects = null)
    {
        ControlFlowVerifier.Validate(input, module, calleeEffects: calleeEffects);
        var graph = input.Copy();
        // Keep OpLoopMerge on a dedicated header. A source loop body can itself
        // begin a selection; that selection cannot share the loop merge instruction.
        foreach (int headerId in graph.Loops.Keys.ToArray()) {
            var header = graph.Blocks.Single(b => b.Id == headerId);
            var body = graph.Block(); body.Instructions.AddRange(header.Instructions);
            header.Instructions.Clear(); body.Terminator = header.Terminator;
            header.Terminator = new ControlFlowTerminator.Branch(new(body.Id));
            if (graph.SelectionMerges.Remove(headerId, out var merge)) graph.SelectionMerges.Add(body.Id, merge);
        }
        ControlFlowVerifier.Validate(graph, module, calleeEffects: calleeEffects);
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
        var arrayLengths = new Dictionary<int, SpirvRuntimeArrayLength>();
        foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Builtin { Function: "arrayLength" })) {
            var builtin = (ValueOperation.Builtin)instruction.Operation;
            var pointer = builtin.Arguments.Single();
            while (definitions[pointer.Id] is ValueOperation.Let let) pointer = let.Value;
            SpirvRuntimeArrayLength length = definitions[pointer.Id] switch {
                ValueOperation.Member member when member.Base.Type is ShaderType.Pointer { Base: ShaderType.Structure structure }
                    => new(member.Base, null, (uint)structure.Members.ToList().FindIndex(m => m.Name == member.Name)),
                ValueOperation.Symbol symbol when module.Globals.Single(g => g.Name == symbol.Name).Type is ShaderType.Array { Length: null }
                    => new(null, symbol.Name, 0),
                _ => throw new ShaderException(DiagnosticStage.SpirvWrite, "Runtime array length requires a prepared buffer member.", instruction.Span)
            };
            arrayLengths.Add(instruction.Result!.Value.Id, length);
        }
        var structural = new HashSet<int>();
        int Target(int? id) {
            if (id is { } target) return target;
            var block = graph.Block(); graph.Blocks.Remove(block); structural.Add(block.Id); return block.Id;
        }
        var selections = graph.SelectionMerges.ToDictionary(p => p.Key, p => Target(p.Value));
        var loops = graph.Loops.ToDictionary(p => p.Key, p => (Continuing: Target(p.Value.Continuing), Merge: Target(p.Value.Merge)));
        var backedges = loops.Where(p => structural.Contains(p.Value.Continuing)).ToDictionary(p => p.Value.Continuing, p => p.Key);
        var blocks = graph.Blocks.ToDictionary(b => b.Id);
        var order = new List<int>(); var visited = new HashSet<int>();
        void Visit(int id, HashSet<int> stops) {
            if (stops.Contains(id) || !visited.Add(id)) return;
            order.Add(id);
            if (structural.Contains(id)) return;
            var block = blocks[id];
            if (loops.TryGetValue(id, out var loop)) {
                var bodyStops = new HashSet<int>(stops) { id, loop.Continuing, loop.Merge };
                foreach (var edge in block.Terminator!.Edges) Visit(edge.Target, bodyStops);
                Visit(loop.Continuing, new HashSet<int>(stops) { id, loop.Merge });
                Visit(loop.Merge, stops);
            }
            else if (selections.TryGetValue(id, out int merge)) {
                var armStops = new HashSet<int>(stops) { merge };
                foreach (var edge in block.Terminator!.Edges) Visit(edge.Target, armStops);
                Visit(merge, stops);
            }
            else foreach (var edge in block.Terminator!.Edges) Visit(edge.Target, stops);
        }
        Visit(graph.Entry, []);
        if (visited.Count != blocks.Count + structural.Count)
            throw new ShaderException(DiagnosticStage.SpirvWrite, "Target CFG traversal omitted a block in " + graph.Signature.Name);
        return new(graph, order.AsReadOnly(), selections.ToFrozenDictionary(), loops.ToFrozenDictionary(), structural.ToFrozenSet(), backedges.ToFrozenDictionary(), arrayLengths.ToFrozenDictionary());
    }
}

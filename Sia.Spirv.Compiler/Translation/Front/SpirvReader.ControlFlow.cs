using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private Dictionary<string, ControlFlowFunction>? nativeGraphs;

        private string? ResolveNativeControlFlow(ICollection<CanonicalPassTrace>? traces)
        {
            // These structured statements supply existing type/effect translation.
            // Native topology and phi identities never pass through Region/Edge copies.
            var allocations = rawFunctions.ToDictionary(f => f.Id, f => new Block());
            foreach (var raw in rawFunctions) {
                allocations[raw.Id].Statements.AddRange(raw.Function.Body.Statements);
                resolvingMeshFunction = raw.Id;
                foreach (var block in raw.Blocks)
                    foreach (var instruction in block.Instructions) {
                        current = instruction; LowerInstruction(block.Body);
                    }
                // Conservative effect summaries include every native block. This is
                // metadata for the temporary expression adapter, never executable IR.
                raw.Function.Body.Statements.AddRange(raw.Blocks.SelectMany(b => b.Body.Statements));
                raw.Function.Body.Statements.AddRange(raw.Blocks.Where(b => b.Terminator is { } t && (Op)t.Opcode is Op.Kill or Op.TerminateInvocation)
                    .Select(b => new Statement.InvocationKill {
                        Span = new(b.Terminator!.WordOffset * 4, b.Terminator.WordCount * 4),
                        ExplicitTermination = (Op)b.Terminator.Opcode == Op.TerminateInvocation
                    }));
            }
            nativeGraphs = new(StringComparer.Ordinal);
            var slotTraces = new List<CanonicalPassTrace>();
            foreach (var raw in rawFunctions) {
                var reader = StructuredControlFlowReader.Native(raw.Function, module);
                var graph = reader.NativeGraph;
                var map = raw.Blocks.Select((block, index) => (block.Id, Block: index == 0 ? reader.NativeCurrent : graph.Block()))
                    .ToDictionary(pair => pair.Id, pair => pair.Block);
                IEnumerable<uint> Targets(RawBlock block) => block.Terminator is { } terminator ? (Op)terminator.Opcode switch {
                    Op.Branch when terminator.Operands.Length >= 1 => [terminator.Operands[0]],
                    Op.BranchConditional when terminator.Operands.Length >= 3 => [terminator.Operands[1], terminator.Operands[2]],
                    Op.Switch when terminator.Operands.Length >= 2 => [terminator.Operands[1], .. terminator.Operands.Skip(3).Where((_, index) => index % 2 == 0)],
                    _ => []
                } : [];
                foreach (var block in raw.Blocks)
                    foreach (var phi in block.Phis) {
                        current = phi;
                        var a = phi.Operands; var value = graph.Value(Type(a[0]));
                        var labels = a.Skip(3).Where((_, index) => index % 2 == 0).ToArray();
                        if (labels.Distinct().Count() != labels.Length) throw Error("Duplicate phi predecessor.");
                        var predecessors = raw.Blocks.Where(b => Targets(b).Contains(block.Id)).Select(b => b.Id).ToHashSet();
                        if (!predecessors.SetEquals(labels)) throw Error("Phi predecessors must match the native CFG edges.");
                        map[block.Id].Parameters.Add(value); reader.NativeBind(Name(a[1], "r"), value);
                    }
                reader.NativeBody(allocations[raw.Id]);
                foreach (var block in raw.Blocks) {
                    reader.NativeBegin(map[block.Id]); reader.NativeBody(block.Body);
                    current = block.Terminator ?? throw Error("Missing native terminator.");
                    var a = current.Operands;
                    ControlFlowEdge Edge(uint target) {
                        if (!map.TryGetValue(target, out var destination)) throw Error($"Branch to undefined block %{target}.");
                        var arguments = new List<SsaValue>();
                        foreach (var phi in raw.Blocks.Single(b => b.Id == target).Phis) {
                            current = phi; var operands = phi.Operands;
                            var incoming = Enumerable.Range(0, (operands.Length - 2) / 2)
                                .Where(i => operands[3 + i * 2] == block.Id).Select(i => operands[2 + i * 2]).ToArray();
                            if (incoming.Length != 1) throw Error(incoming.Length == 0 ? "Missing phi predecessor." : "Duplicate phi predecessor.");
                            if (!values.TryGetValue(incoming[0], out var expression) || expression.Type != Type(operands[0]))
                                throw Error("Undefined or mismatched native phi value.");
                            arguments.Add(expression.Type is ShaderType.Pointer ? reader.NativeAddress(expression) : reader.NativeValue(expression));
                        }
                        return new(destination.Id, arguments);
                    }
                    var op = (Op)current.Opcode;
                    ControlFlowTerminator terminator;
                    switch (op) {
                        case Op.Branch:
                            Count(1, 1); terminator = new ControlFlowTerminator.Branch(Edge(a[0])); break;
                        case Op.BranchConditional:
                            Count(3, 5); var condition = reader.NativeValue(Value(a[0]));
                            terminator = new ControlFlowTerminator.Conditional(condition, Edge(a[1]), Edge(a[2])); break;
                        case Op.Switch:
                            Count(2);
                            if ((a.Length - 2) % 2 != 0) throw Error("64-bit switch selectors are not supported yet.");
                            var selector = reader.NativeValue(Value(a[0])); var cases = new List<ControlFlowCase>();
                            for (int i = 2; i < a.Length; i += 2) {
                                var literal = DecodeLiteral(selector.Type, a.AsSpan(i, 1)) as Expression.Literal ?? throw Error("Invalid switch value.");
                                cases.Add(new([literal], Edge(a[i + 1])));
                            }
                            terminator = new ControlFlowTerminator.Switch(selector, cases, Edge(a[1])); break;
                        case Op.Return:
                            Count(0, 0); terminator = new ControlFlowTerminator.Return { Span = new(current.WordOffset * 4, current.WordCount * 4) }; break;
                        case Op.ReturnValue:
                            if (raw.Function.ReturnType is ShaderType.Pointer && a.Length != 1)
                                throw Error("Invalid native pointer return operands.");
                            Count(1, 1);
                            if (!values.TryGetValue(a[0], out var returned) || returned.Type != raw.Function.ReturnType)
                                throw Error(raw.Function.ReturnType is ShaderType.Pointer
                                    ? "Native return references an undefined pointer or mismatched pointer type."
                                    : "Native return references an undefined value or mismatched return type.");
                            terminator = new ControlFlowTerminator.Return(returned.Type is ShaderType.Pointer
                                ? reader.NativeAddress(returned) : reader.NativeValue(returned)) { Span = new(current.WordOffset * 4, current.WordCount * 4) }; break;
                        case Op.Unreachable:
                            Count(0, 0); terminator = new ControlFlowTerminator.Unreachable(new(current.WordOffset * 4, current.WordCount * 4)); break;
                        case Op.Kill: case Op.TerminateInvocation:
                            Count(0, 0); ValidateInvocationFeature(op);
                            terminator = new ControlFlowTerminator.InvocationKill(new(current.WordOffset * 4, current.WordCount * 4),
                                ExplicitTermination: op == Op.TerminateInvocation); break;
                        default: throw Error("Native terminator requires canonical migration.");
                    }
                    reader.NativeCurrent.Terminator = terminator;
                    if (block.Continuing is uint continuing) {
                        if (block.Merge is not uint merge || !map.ContainsKey(continuing) || !map.ContainsKey(merge))
                            throw Error("Undefined native loop merge/continue block.");
                        graph.Loops.Add(map[block.Id].Id, new(map[continuing].Id, map[merge].Id));
                    }
                    else if (block.Merge is uint merge) {
                        if (!map.ContainsKey(merge)) throw Error("Undefined native selection merge block.");
                        graph.SelectionMerges.Add(map[block.Id].Id, map[merge].Id);
                    }
                }
                ControlFlowAnalysis.RemoveUnreachable(graph);
                ControlFlowVerifier.Validate(graph, module);
                NativePointerValidator.ValidateBeforeExpansion(graph, module, fullVariablePointers);
                bool slots = graph.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Local
                    && i.Result?.Type is ShaderType.Pointer { Base: ShaderType.Pointer });
                if (slots) {
                    string? before = traces is null ? null : ControlFlowPrinter.Write(graph);
                    LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
                    if (traces is not null) slotTraces.Add(new(raw.Function.Name, "native-slot-promotion", before!, ControlFlowPrinter.Write(graph),
                        "loaded address snapshots, source effects and native CFG topology", "SSA definitions and address origins"));
                }
                nativeGraphs.Add(raw.Function.Name, graph);
            }
            var privateHelpers = nativeGraphs.Where(p => p.Value.Blocks.SelectMany(b => b.Instructions).Any(i =>
                i.Operation is ValueOperation.Symbol && i.Result?.Type is ShaderType.Pointer { Space: AddressSpace.Private, Base: ShaderType.Pointer }))
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            bool privateChanged;
            do {
                privateChanged = false;
                foreach (var (name, graph) in nativeGraphs)
                    if (graph.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Call c && privateHelpers.Contains(c.Function)))
                        privateChanged |= privateHelpers.Add(name);
            } while (privateChanged);
            if (privateHelpers.Count != 0 || nativeGraphs.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions).Any(i =>
                i.Operation is ValueOperation.Call call && call.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.Pointer }))) {
                // Bind the actual slot address before shared promotion. The same CFG
                // copier handles void/nested helpers and pointer-return helpers; no
                // pointee copy-in/copy-out or frontend provenance solver is involved.
                var originalGraphs = nativeGraphs;
                nativeGraphs = new(StringComparer.Ordinal);
                foreach (var (name, graph) in originalGraphs) {
                    string? before = traces is null ? null : ControlFlowPrinter.Write(graph);
                    var expanded = CanonicalHelperInliner.Run(graph, module, originalGraphs, expandPointerSlots: true, expandFunctions: privateHelpers);
                    LocalValuePromotion.Run(expanded); ControlFlowVerifier.Validate(expanded, module);
                    nativeGraphs.Add(name, expanded);
                    if (traces is not null && !ReferenceEquals(graph, expanded)) slotTraces.Add(new(name,
                        "native-slot-helper-expansion", before!, ControlFlowPrinter.Write(expanded),
                        "argument snapshots, actual slot addresses and callee effect order", "CFG topology, SSA definitions and call summaries"));
                }
                var remainingCalls = nativeGraphs.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions)
                    .Select(i => i.Operation).OfType<ValueOperation.Call>().Select(c => c.Function).ToHashSet(StringComparer.Ordinal);
                foreach (var raw in rawFunctions.Where(f => (f.Function.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.Pointer })
                    || privateHelpers.Contains(f.Function.Name)) && !remainingCalls.Contains(f.Function.Name) && !entries.Any(e => e.Id == f.Id))) {
                    nativeGraphs.Remove(raw.Function.Name); module.Functions.Remove(raw.Function);
                }
            }
            foreach (var graph in nativeGraphs.Values)
                foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions))
                    if (instruction is { Operation: ValueOperation.Local or ValueOperation.Symbol, Result: { Type: ShaderType.Pointer { Base: ShaderType.Pointer } } slot }
                        && slot.Type is ShaderType.Pointer { Space: AddressSpace.Function or AddressSpace.Private }
                        && (instruction.Operation is ValueOperation.Local || module.Globals.Any(g => g.Name == ((ValueOperation.Symbol)instruction.Operation).Name))
                        && !LocalValuePromotion.IsDefinitelyAssigned(graph, slot))
                        throw new ShaderException(DiagnosticStage.SpirvParse, "Native " + graph.Signature.Name + ": pointer slot has no initialized finite provenance on every incoming path.");
            // Shared validation and passes consume native graphs directly. The
            // public Module is materialized once at the explicit output adapter.
            if (traces is not null) foreach (var trace in slotTraces) traces.Add(trace);
            return null;
        }
    }
}

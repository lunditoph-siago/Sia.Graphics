using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Expose high-level query state and robustness guards before SPIR-V serialization.</summary>
internal static class SpirvRayQueryLowering
{
    private static bool HighLevel(string name) => name.StartsWith("rayQuery", StringComparison.Ordinal)
        || name is "getCommittedHitVertexPositions" or "getCandidateHitVertexPositions";

    internal static CanonicalModule Run(CanonicalModule input)
    {
        ModuleValidator.Validate(input, native: true);
        var graphs = input.Functions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var deferred = input.DeferredFunctions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var effects = ShaderEffectAnalysis.Compute(input);
        foreach (var signature in input.Declarations.Functions) {
            // Only an explicit deferral may read declaration statements. Import
            // it only when this target pass can actually take ownership.
            if (!graphs.TryGetValue(signature.Name, out var graph)
                && !StructuredControlFlowReader.TryRead(signature, input.Declarations, out graph, out _, effects, native: true)) continue;
            if (!graph!.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Builtin b && HighLevel(b.Function))) continue;
            graphs[signature.Name] = new FunctionLowering(graph, input.Declarations, effects).Run();
            deferred.Remove(signature.Name);
        }
        var output = new CanonicalModule(input.Declarations, graphs, deferred, input.EntryFunctions);
        effects = ShaderEffectAnalysis.Compute(output);
        foreach (var pair in graphs.ToArray()) {
            var graph = pair.Value;
            if (!graph.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Call c
                && (i.Effects & effects[c.Function]) != effects[c.Function])) continue;
            if (input.Functions.TryGetValue(pair.Key, out var original) && ReferenceEquals(original, graph)) graphs[pair.Key] = graph = graph.Copy();
            foreach (var block in graph.Blocks)
                for (int i = 0; i < block.Instructions.Count; i++)
                    if (block.Instructions[i].Operation is ValueOperation.Call call)
                        block.Instructions[i] = block.Instructions[i] with { Operation = call with { CalleeEffects = call.CalleeEffects | effects[call.Function] } };
        }
        ModuleValidator.Validate(output, native: true);
        return output;
    }

    private sealed class FunctionLowering
    {
        private readonly ControlFlowFunction graph;
        private readonly Module module;
        private readonly IReadOnlyDictionary<string, ShaderEffects> calleeEffects;
        private readonly Dictionary<int, ControlFlowInstruction> definitions;
        private readonly Dictionary<int, (SsaValue State, SsaValue Min, SsaValue Max)> trackers = [];
        private readonly HashSet<string> names;
        private ControlFlowBlock current = null!;
        private ControlFlowInstruction origin = null!;
        private int nextName;

        internal FunctionLowering(ControlFlowFunction input, Module module, IReadOnlyDictionary<string, ShaderEffects> calleeEffects)
        {
            graph = input.Copy(); this.module = module; this.calleeEffects = calleeEffects;
            definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
            names = module.Globals.Select(g => g.Name).Concat(module.Constants.Select(c => c.Name))
                .Concat(module.Functions.Select(f => f.Name)).Concat(input.Signature.Arguments.Select(a => a.Name))
                .Concat(definitions.Values.Select(i => i.Operation switch { ValueOperation.Local l => l.Name, ValueOperation.Symbol s => s.Name, _ => "" }))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var call in definitions.Values.Select(i => i.Operation).OfType<ValueOperation.Builtin>().Where(b => HighLevel(b.Function))) Track(call.Arguments[0]);
            foreach (var call in graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is null)
                .Select(i => i.Operation).OfType<ValueOperation.Builtin>().Where(b => HighLevel(b.Function))) Track(call.Arguments[0]);
        }

        private int Root(SsaValue value) => definitions.TryGetValue(value.Id, out var instruction) ? instruction.Operation switch {
            ValueOperation.Local when value.Type is ShaderType.Pointer { Base: ShaderType.RayQuery } => value.Id,
            ValueOperation.Let alias => Root(alias.Value),
            _ => -1
        } : -1;
        private void Track(SsaValue query)
        {
            int root = Root(query);
            if (root < 0 || trackers.ContainsKey(root)) return;
            trackers.Add(root, (graph.Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function)),
                graph.Value(new ShaderType.Pointer(ShaderType.F32, AddressSpace.Function)), graph.Value(new ShaderType.Pointer(ShaderType.F32, AddressSpace.Function))));
        }

        internal ControlFlowFunction Run()
        {
            foreach (var block in graph.Blocks.ToArray()) {
                var instructions = block.Instructions.ToArray(); block.Instructions.Clear();
                var terminator = block.Terminator; block.Terminator = null;
                bool selection = graph.SelectionMerges.Remove(block.Id, out int? merge);
                current = block;
                foreach (var instruction in instructions) {
                    origin = instruction;
                    if (instruction.Operation is ValueOperation.Builtin call && HighLevel(call.Function)) {
                        Lower(call, instruction.Result); continue;
                    }
                    current.Instructions.Add(instruction);
                    if (instruction.Result is { } query && trackers.TryGetValue(query.Id, out var tracker)) {
                        foreach (var slot in new[] { tracker.State, tracker.Min, tracker.Max }) {
                            Declare(slot); Store(slot, Zero(((ShaderType.Pointer)slot.Type).Base));
                        }
                    }
                }
                current.Terminator = terminator;
                if (selection) graph.SelectionMerges.Add(current.Id, merge);
            }
            // Ordinary companion slots participate in the existing SSA promotion;
            // the opaque query allocation can never be loaded, stored or promoted.
            ControlFlowVerifier.Validate(graph, module, calleeEffects: calleeEffects);
            LocalValuePromotion.Run(graph);
            return graph;
        }

        private void Add(SsaValue? result, ValueOperation operation) => current.Instructions.Add(new(result, operation, origin.Span) { DiagnosticFilters = origin.DiagnosticFilters });
        private SsaValue Emit(ShaderType type, ValueOperation operation) { var value = graph.Value(type); Add(value, operation); return value; }
        private SsaValue U(uint value) => Emit(ShaderType.U32, new ValueOperation.Literal(value));
        private SsaValue Zero(ShaderType type) => Emit(type, new ValueOperation.Construct([]));
        private SsaValue Binary(string op, SsaValue a, SsaValue b, ShaderType? type = null) => Emit(type ?? ShaderType.Bool, new ValueOperation.Binary(op, a, b));
        private SsaValue And(SsaValue a, SsaValue b) => Binary("&&", a, b);
        private SsaValue Not(SsaValue value) => Emit(ShaderType.Bool, new ValueOperation.Unary("!", value));
        private SsaValue Flag(SsaValue state, uint mask) => Binary("!=", Binary("&", state, U(mask), ShaderType.U32), U(0));
        private SsaValue Load(SsaValue slot) => Emit(((ShaderType.Pointer)slot.Type).Base, new ValueOperation.Load(slot));
        private void Store(SsaValue slot, SsaValue value) => Add(null, new ValueOperation.Store(slot, value));
        private void Declare(SsaValue slot) {
            string name; do name = "sia_spv_query_" + nextName++; while (!names.Add(name));
            Add(slot, new ValueOperation.Local(name, false));
        }
        private SsaValue Slot(ShaderType type, SsaValue initial) {
            var slot = graph.Value(new ShaderType.Pointer(type, AddressSpace.Function)); Declare(slot); Store(slot, initial); return slot;
        }
        private SsaValue Raw(string name, ShaderType type, params SsaValue[] args) => Emit(type, new ValueOperation.Builtin("spirvRayQuery" + name + "KHR", args, type));
        private void Raw(string name, params SsaValue[] args) => Add(null, new ValueOperation.Builtin("spirvRayQuery" + name + "KHR", args, new ShaderType.Void()));
        private SsaValue Read(string name, ShaderType type, SsaValue query, bool committed) => Raw(name, type, query, U(committed ? 1u : 0u));
        private void Guard(SsaValue condition, Action body) {
            var accept = graph.Block(); var done = graph.Block();
            graph.SelectionMerges.Add(current.Id, done.Id);
            current.Terminator = new ControlFlowTerminator.Conditional(condition, new(accept.Id), new(done.Id));
            current = accept; body(); current.Terminator = new ControlFlowTerminator.Branch(new(done.Id)); current = done;
        }
        private void Result(SsaValue? result, SsaValue value) { if (result is { } target) Add(target, new ValueOperation.Let("", value)); }

        private void Lower(ValueOperation.Builtin call, SsaValue? result)
        {
            var args = call.Arguments; var query = args[0];
            if (!trackers.TryGetValue(Root(query), out var tracker))
                throw new ShaderException(DiagnosticStage.SpirvWrite, "Ray query operations require a local query.", origin.Span);
            var state = Load(tracker.State);
            SsaValue Active() => And(Flag(state, 2), Not(Flag(state, 4)));
            SsaValue Ready(bool committed) => And(Flag(state, 2), committed ? Flag(state, 4) : Not(Flag(state, 4)));
            switch (call.Function) {
                case "rayQueryInitialize": {
                    var fields = RayQueryTypes.Descriptor.Members.Select(m => Emit(m.Type, new ValueOperation.Member(args[2], m.Name))).ToArray();
                    var valid = And(Binary("<=", fields[2], fields[3]), Binary(">=", fields[2], Zero(ShaderType.F32)));
                    foreach (int vector in new[] { 4, 5 }) {
                        var bools = new ShaderType.Vector(3, ShaderType.Bool);
                        SsaValue Test(string name) => Emit(ShaderType.Bool, new ValueOperation.Builtin("any",
                            [Emit(bools, new ValueOperation.Builtin(name, [fields[vector]], bools))], ShaderType.Bool));
                        valid = And(valid, Not(Binary("||", Test("isNan"), Test("isInf"))));
                    }
                    foreach (uint[] group in new uint[][] { [256, 512], [256, 16, 32], [1, 2, 64, 128] })
                        for (int i = 0; i < group.Length; i++) for (int j = i + 1; j < group.Length; j++)
                            valid = And(valid, Not(And(Flag(fields[0], group[i]), Flag(fields[0], group[j]))));
                    Store(tracker.Max, fields[3]);
                    Guard(valid, () => {
                        Raw("Initialize", query, args[1], fields[0], fields[1], fields[4], fields[2], fields[5], fields[3]);
                        Store(tracker.Min, fields[2]); Store(tracker.State, U(1));
                    }); break;
                }
                case "rayQueryProceed": {
                    var slot = Slot(ShaderType.Bool, Zero(ShaderType.Bool));
                    Guard(And(Flag(state, 1), Not(Flag(state, 4))), () => {
                        var proceed = Raw("Proceed", ShaderType.Bool, query); Store(slot, proceed);
                        var flags = Emit(ShaderType.U32, new ValueOperation.Select(proceed, U(2), U(6)));
                        Store(tracker.State, Binary("|", state, flags, ShaderType.U32));
                    }); Result(result, Load(slot)); break;
                }
                case "rayQueryTerminate": Guard(Active(), () => Raw("Terminate", query)); break;
                case "rayQueryConfirmIntersection":
                    Guard(Active(), () => Guard(Binary("==", Read("GetIntersectionType", ShaderType.U32, query, false), U(0)), () => Raw("ConfirmIntersection", query))); break;
                case "rayQueryGenerateIntersection":
                    Guard(Active(), () => {
                        var candidate = Read("GetIntersectionType", ShaderType.U32, query, false);
                        var latest = Slot(ShaderType.F32, Load(tracker.Max));
                        Guard(Binary("!=", Read("GetIntersectionType", ShaderType.U32, query, true), U(0)),
                            () => Store(latest, Read("GetIntersectionT", ShaderType.F32, query, true)));
                        var inRange = And(Binary(">=", args[1], Load(tracker.Min)), Binary("<=", args[1], Load(latest)));
                        Guard(And(inRange, Binary("==", candidate, U(1))), () => Raw("GenerateIntersection", query, args[1]));
                    }); break;
                case "rayQueryGetCommittedIntersection": case "rayQueryGetCandidateIntersection": {
                    bool committed = call.Function == "rayQueryGetCommittedIntersection";
                    var type = (ShaderType.Structure)call.ReturnType; var slot = Slot(type, Zero(type));
                    void Member(int index, SsaValue value) => Store(Emit(new ShaderType.Pointer(type.Members[index].Type, AddressSpace.Function),
                        new ValueOperation.Member(slot, type.Members[index].Name)), value);
                    Guard(Ready(committed), () => {
                        var raw = Read("GetIntersectionType", ShaderType.U32, query, committed);
                        var kind = committed ? raw : Emit(ShaderType.U32, new ValueOperation.Select(Binary("==", raw, U(0)), U(1), U(3)));
                        Member(0, kind);
                        Guard(Binary("!=", kind, U(0)), () => {
                            string[] common = ["GetIntersectionInstanceCustomIndex", "GetIntersectionInstanceId", "GetIntersectionInstanceShaderBindingTableRecordOffset", "GetIntersectionGeometryIndex", "GetIntersectionPrimitiveIndex"];
                            for (int i = 0; i < common.Length; i++) Member(i + 2, Read(common[i], type.Members[i + 2].Type, query, committed));
                            Member(9, Read("GetIntersectionObjectToWorld", type.Members[9].Type, query, committed));
                            Member(10, Read("GetIntersectionWorldToObject", type.Members[10].Type, query, committed));
                            if (committed) Member(1, Read("GetIntersectionT", ShaderType.F32, query, true));
                            Guard(Binary("==", kind, U(1)), () => {
                                if (!committed) Member(1, Read("GetIntersectionT", ShaderType.F32, query, false));
                                Member(7, Read("GetIntersectionBarycentrics", type.Members[7].Type, query, committed));
                                Member(8, Read("GetIntersectionFrontFace", ShaderType.Bool, query, committed));
                            });
                        });
                    }); Result(result, Load(slot)); break;
                }
                case "getCommittedHitVertexPositions": case "getCandidateHitVertexPositions": {
                    bool committed = call.Function == "getCommittedHitVertexPositions";
                    var slot = Slot(call.ReturnType, Zero(call.ReturnType));
                    Guard(Ready(committed), () => Guard(Binary("==", Read("GetIntersectionType", ShaderType.U32, query, committed), U(committed ? 1u : 0u)),
                        () => Store(slot, Read("GetIntersectionTriangleVertexPositions", call.ReturnType, query, committed))));
                    Result(result, Load(slot)); break;
                }
                default: throw new ShaderException(DiagnosticStage.SpirvWrite, "Unknown ray query operation.", origin.Span);
            }
        }
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Promote definitely initialized non-escaping data/address slots into SSA block parameters. Recompute analyses after rewriting.</summary>
internal static class LocalValuePromotion
{
    internal static bool IsDefinitelyAssigned(ControlFlowFunction function, SsaValue slot,
        IReadOnlyDictionary<int, IReadOnlyList<(ControlFlowBlock Block, ControlFlowEdge Edge)>>? predecessors = null) {
        predecessors ??= new ControlFlowAnalysisContext(function).Predecessors;
        var aliases = new HashSet<int> { slot.Id };
        if (slot.Type is ShaderType.Pointer { Base: ShaderType.Pointer }) {
            var definitions = function.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null)
                .ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
            if (definitions.GetValueOrDefault(slot.Id) is ValueOperation.Symbol symbol)
                aliases.UnionWith(definitions.Where(p => p.Value is ValueOperation.Symbol s && s.Name == symbol.Name).Select(p => p.Key));
            bool added;
            do {
                added = false;
                foreach (var (id, operation) in definitions)
                    if (operation is ValueOperation.Let alias && aliases.Contains(alias.Value.Id)) added |= aliases.Add(id);
            } while (added);
        }
        // Greatest fixed point for definite assignment. The entry starts empty;
        // every other block intersects its predecessors, including loop backedges.
        // A dynamic allocation resets the slot at its actual instruction position.
        var assigned = function.Blocks.ToDictionary(b => b.Id, _ => true);
        bool Incoming(ControlFlowBlock block) => block.Id != function.Entry && predecessors[block.Id].Count != 0
            && predecessors[block.Id].All(p => assigned[p.Block.Id]);
        bool Transfer(ControlFlowBlock block, bool state, bool checkLoads) {
            foreach (var instruction in block.Instructions) {
                if (instruction.Result == slot && instruction.Operation is ValueOperation.Local local) state = local.ZeroInitialize;
                else if (instruction.Operation is ValueOperation.Store store && aliases.Contains(store.Pointer.Id)) state = true;
                else if (checkLoads && instruction.Operation is ValueOperation.Load load && aliases.Contains(load.Pointer.Id) && !state) return false;
            }
            return checkLoads || state;
        }
        bool changed;
        do {
            changed = false;
            foreach (var block in function.Blocks) {
                bool state = Transfer(block, Incoming(block), checkLoads: false);
                if (assigned[block.Id] != state) { assigned[block.Id] = state; changed = true; }
            }
        } while (changed);
        return function.Blocks.All(block => Transfer(block, Incoming(block), checkLoads: true));
    }

    public static void Run(ControlFlowFunction function, ControlFlowAnalysisContext? analyses = null)
    {
        analyses?.RequireFunction(function);
        var instructions = function.Blocks.SelectMany(b => b.Instructions).ToArray();
        var predecessors = (analyses ?? new ControlFlowAnalysisContext(function)).Predecessors;
        var slots = instructions.Where(i => i.Operation is ValueOperation.Local local && i.Result?.Type is ShaderType.Pointer pointer
                && (CanonicalTypes.Data(pointer.Base) || !local.ZeroInitialize && (pointer.Base is ShaderType.Pointer || CanonicalTypes.Resource(pointer.Base))))
            .Select(i => i.Result!.Value).Where(slot => !function.Blocks.Any(b => b.Terminator!.Operands.Contains(slot)
                || b.Terminator.Edges.Any(e => e.Arguments.Contains(slot))) && instructions.All(i => !i.Operation.Operands.Contains(slot)
                || i.Operation is ValueOperation.Load { MemoryAccess: null } load && load.Pointer == slot
                || i.Operation is ValueOperation.Store { MemoryAccess: null } store && store.Pointer == slot && store.Value != slot)
                && IsDefinitelyAssigned(function, slot, predecessors))
            .ToDictionary(v => v.Id);
        if (slots.Count == 0) return;
        // Materialize implicit zero only for value promotion. Escaping/qualified
        // locals retain their original declaration and explicit memory operations.
        foreach (var block in function.Blocks) {
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                if (instruction.Operation is not ValueOperation.Local { ZeroInitialize: true } || instruction.Result is not { } slot || !slots.ContainsKey(slot.Id)) continue;
                var zero = function.Value(((ShaderType.Pointer)slot.Type).Base);
                block.Instructions.Insert(++i, new(zero, new ValueOperation.Construct([]), instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters });
                block.Instructions.Insert(++i, new(null, new ValueOperation.Store(slot, zero), instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters });
            }
        }
        var last = new Dictionary<(int Slot, int Block), SsaValue>();
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.Operation is ValueOperation.Store store && slots.ContainsKey(store.Pointer.Id)) last[(store.Pointer.Id, block.Id)] = store.Value;
        var incoming = new Dictionary<(int Slot, int Block), SsaValue>();
        var replacements = new Dictionary<int, SsaValue>();
        SsaValue ReadIncoming(int slot, ControlFlowBlock block) {
            if (incoming.TryGetValue((slot, block.Id), out var found)) return found;
            var edges = predecessors[block.Id];
            if (edges.Count == 0) throw new ShaderException(DiagnosticStage.Validation, "Uninitialized promoted local in b" + block.Id);
            // Install the parameter before following backedges, including mutually recursive merges.
            var parameter = function.Value(((ShaderType.Pointer)slots[slot].Type).Base);
            incoming.Add((slot, block.Id), parameter); block.Parameters.Add(parameter);
            foreach (var (predecessor, edge) in edges)
                edge.Arguments.Add(last.TryGetValue((slot, predecessor.Id), out var value) ? value : ReadIncoming(slot, predecessor));
            return parameter;
        }
        foreach (var block in function.Blocks) {
            var local = new Dictionary<int, SsaValue>(); var rewritten = new List<ControlFlowInstruction>();
            foreach (var instruction in block.Instructions) {
                switch (instruction.Operation) {
                    case ValueOperation.Local when instruction.Result is { } slotValue && slots.ContainsKey(slotValue.Id): break;
                    case ValueOperation.Store store when slots.ContainsKey(store.Pointer.Id): local[store.Pointer.Id] = store.Value; break;
                    case ValueOperation.Load load when slots.ContainsKey(load.Pointer.Id):
                        replacements.Add(instruction.Result!.Value.Id, local.TryGetValue(load.Pointer.Id, out var value) ? value : ReadIncoming(load.Pointer.Id, block)); break;
                    default: rewritten.Add(instruction); break;
                }
            }
            block.Instructions.Clear(); block.Instructions.AddRange(rewritten);
        }
        SsaValue Resolve(SsaValue value) {
            var seen = new HashSet<int>();
            while (replacements.TryGetValue(value.Id, out var mapped)) {
                if (!seen.Add(value.Id)) throw new ShaderException(DiagnosticStage.Validation, "Cyclic SSA replacement");
                value = mapped;
            }
            return value;
        }
        foreach (var block in function.Blocks) {
            for (int i = 0; i < block.Instructions.Count; i++) block.Instructions[i] = block.Instructions[i] with { Operation = block.Instructions[i].Operation.Map(Resolve) };
            block.Terminator = block.Terminator!.Map(Resolve);
            foreach (var edge in block.Terminator.Edges)
                for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Resolve(edge.Arguments[i]);
        }
        // Existing edges and block topology survive; their SSA argument lists change.
        analyses?.Preserve(ControlFlowAnalyses.All);
    }
}

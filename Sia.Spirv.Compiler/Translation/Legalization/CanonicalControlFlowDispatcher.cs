using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// A target region for arbitrary data CFGs. Frontends retain their original edges;
// this pass owns the dispatcher and parallel edge copies needed by structured outputs.
internal static class CanonicalControlFlowDispatcher
{
    internal static ControlFlowFunction Run(ControlFlowFunction input)
    {
        var topology = input.Blocks.ToDictionary(b => b.Id);
        var visited = new HashSet<int>(); var active = new HashSet<int>();
        bool Cyclic(int id) {
            if (active.Contains(id)) return true;
            if (!visited.Add(id)) return false;
            active.Add(id);
            foreach (var edge in topology[id].Terminator!.Edges) if (Cyclic(edge.Target)) return true;
            active.Remove(id); return false;
        }
        if (!input.Blocks.Any(b => b.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch) && !Cyclic(input.Entry))
            return input.Copy();
        var entry = input.Blocks.Single(b => b.Id == input.Entry);
        if (entry.Terminator is not ControlFlowTerminator.Branch start || entry.Parameters.Count != 0
            || input.Blocks.Any(b => b.Terminator!.Edges.Any(e => e.Target == input.Entry)))
            throw new ShaderException(DiagnosticStage.Validation, "Unstructured CFG requires a distinct initialization block.");
        var output = input.Copy(); output.Blocks.Clear(); output.SelectionMerges.Clear(); output.Loops.Clear();
        var prologue = new ControlFlowBlock(entry.Id); output.Blocks.Add(prologue); output.Entry = prologue.Id;
        prologue.Instructions.AddRange(entry.Instructions);
        var owners = input.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value))
            .Select(v => (v.Id, b.Id))).ToDictionary(p => p.Item1, p => p.Item2);
        var types = input.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)))
            .ToDictionary(v => v.Id, v => v.Type);
        var spill = input.Blocks.Where(b => b.Id != input.Entry).SelectMany(b => b.Parameters.Select(v => v.Id)).ToHashSet();
        foreach (var block in input.Blocks) foreach (var value in block.Instructions.SelectMany(i => i.Operation.Operands)
            .Concat(block.Terminator!.Operands).Concat(block.Terminator.Edges.SelectMany(e => e.Arguments)))
            if (owners[value.Id] != block.Id && owners[value.Id] != input.Entry) spill.Add(value.Id);
        SsaValue Emit(ControlFlowBlock block, ShaderType type, ValueOperation operation, SourceSpan span = default) {
            var value = output.Value(type); block.Instructions.Add(new(value, operation, span)); return value;
        }
        var slots = new Dictionary<int, SsaValue>();
        foreach (int id in spill.Order()) {
            if (!CanonicalTypes.Data(types[id])) throw new ShaderException(DiagnosticStage.Validation, "Unstructured opaque or pointer values require further target legalization.");
            slots.Add(id, Emit(prologue, new ShaderType.Pointer(types[id], AddressSpace.Function), new ValueOperation.Local("sia_edge_" + id, false)));
        }
        var initialTarget = topology[start.Edge.Target];
        for (int i = 0; i < start.Edge.Arguments.Count; i++)
            prologue.Instructions.Add(new(null, new ValueOperation.Store(slots[initialTarget.Parameters[i].Id], start.Edge.Arguments[i])));
        var pc = Emit(prologue, new ShaderType.Pointer(ShaderType.I32, AddressSpace.Function), new ValueOperation.Local("sia_dispatch", false));
        var initial = Emit(prologue, ShaderType.I32, new ValueOperation.Literal(start.Edge.Target));
        prologue.Instructions.Add(new(null, new ValueOperation.Store(pc, initial)));
        var header = output.Block(); var dispatch = output.Block(); var continuing = output.Block(); var merge = output.Block();
        prologue.Terminator = new ControlFlowTerminator.Branch(new(header.Id));
        header.Terminator = new ControlFlowTerminator.Branch(new(dispatch.Id));
        continuing.Terminator = new ControlFlowTerminator.Branch(new(header.Id));
        output.Loops.Add(header.Id, new(continuing.Id, merge.Id));
        if (input.Signature.ReturnType is ShaderType.Void) merge.Terminator = new ControlFlowTerminator.Return();
        else {
            if (!CanonicalTypes.Data(input.Signature.ReturnType)) throw new ShaderException(DiagnosticStage.Validation, "Unstructured return requires further target legalization.");
            merge.Terminator = new ControlFlowTerminator.Return(Emit(merge, input.Signature.ReturnType, new ValueOperation.Construct([])));
        }
        var blocks = input.Blocks.Where(b => b.Id != input.Entry).ToDictionary(b => b.Id, b => {
            var copy = new ControlFlowBlock(b.Id); output.Blocks.Add(copy); return copy;
        });
        SsaValue Use(SsaValue value, ControlFlowBlock block) => slots.TryGetValue(value.Id, out var slot)
            ? Emit(block, value.Type, new ValueOperation.Load(slot)) : value;
        ControlFlowEdge Schedule(ControlFlowEdge edge, int join) {
            var block = output.Block(); var target = input.Blocks.Single(b => b.Id == edge.Target);
            var values = edge.Arguments.Select(v => Use(v, block)).ToArray();
            // All incoming values are read before any destination parameter changes.
            for (int i = 0; i < values.Length; i++) block.Instructions.Add(new(null, new ValueOperation.Store(slots[target.Parameters[i].Id], values[i])));
            var next = Emit(block, ShaderType.I32, new ValueOperation.Literal(edge.Target));
            block.Instructions.Add(new(null, new ValueOperation.Store(pc, next)));
            block.Terminator = new ControlFlowTerminator.Branch(new(join)); return new(block.Id);
        }
        foreach (var original in input.Blocks.Where(b => b.Id != input.Entry)) {
            var block = blocks[original.Id];
            foreach (var instruction in original.Instructions) {
                block.Instructions.Add(instruction with { Operation = instruction.Operation.Map(v => Use(v, block)) });
                if (instruction.Result is { } value && slots.TryGetValue(value.Id, out var slot))
                    block.Instructions.Add(new(null, new ValueOperation.Store(slot, value), instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters });
            }
            var terminator = original.Terminator!.Map(v => Use(v, block));
            int join = continuing.Id;
            if (terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch) {
                var selectionMerge = output.Block(); selectionMerge.Terminator = new ControlFlowTerminator.Branch(new(continuing.Id));
                join = selectionMerge.Id; output.SelectionMerges.Add(block.Id, join);
            }
            block.Terminator = terminator switch {
                ControlFlowTerminator.Branch branch => new ControlFlowTerminator.Branch(Schedule(branch.Edge, join)),
                ControlFlowTerminator.Conditional condition => condition with { Accept = Schedule(condition.Accept, join), Reject = Schedule(condition.Reject, join) },
                ControlFlowTerminator.Switch selection => selection with { Cases = selection.Cases.Select(c => c with { Edge = Schedule(c.Edge, join) }).ToArray(), Default = Schedule(selection.Default, join) },
                _ => terminator
            };
        }
        var selector = Emit(dispatch, ShaderType.I32, new ValueOperation.Load(pc));
        dispatch.Terminator = new ControlFlowTerminator.Switch(selector,
            blocks.OrderBy(p => p.Key).Select(p => new ControlFlowCase([Expression.I32(p.Key)], new(p.Value.Id))).ToArray(), new(merge.Id));
        output.SelectionMerges.Add(dispatch.Id, continuing.Id);
        ControlFlowAnalysis.RemoveUnreachable(output);
        return output;
    }
}

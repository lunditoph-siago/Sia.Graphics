using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static class AggregateReadPromotion
{
    private sealed record Address(SsaValue Root, IReadOnlyList<ControlFlowInstruction> Path);

    internal static void Run(ControlFlowFunction graph, bool dynamicCompositeIndexing = false)
    {
        var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
        var definitions = instructions.Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
        var roots = instructions.Where(i => i.Operation is ValueOperation.Local && i.Result?.Type is ShaderType.Pointer {
            Space: AddressSpace.Function, Base: ShaderType.Vector or ShaderType.Matrix or ShaderType.Structure or ShaderType.Array
        } pointer
                && CanonicalTypes.Data(pointer.Base) && ShaderMemoryRequirements.TypeMemory(pointer.Base) == MemoryDecorations.None)
            .Select(i => i.Result!.Value).ToDictionary(v => v.Id);
        if (roots.Count == 0) return;
        var addresses = roots.ToDictionary(p => p.Key, p => new Address(p.Value, []));
        bool changed;
        do {
            changed = false;
            foreach (var instruction in instructions) {
                if (instruction.Result is not { Type: ShaderType.Pointer } value || addresses.ContainsKey(value.Id)) continue;
                var parent = instruction.Operation switch {
                    ValueOperation.Let alias => alias.Value,
                    ValueOperation.Access access => access.Base,
                    ValueOperation.Member member => member.Base,
                    _ => (SsaValue?)null
                };
                if (parent is not { } source || !addresses.TryGetValue(source.Id, out var address)) continue;
                var path = instruction.Operation is ValueOperation.Let ? address.Path : address.Path.Append(instruction).ToArray();
                addresses.Add(value.Id, new(address.Root, path)); changed = true;
            }
        } while (changed);
        var eligible = roots.Keys.ToHashSet();
        foreach (var instruction in instructions)
            foreach (var operand in instruction.Operation.Operands) {
                if (!addresses.TryGetValue(operand.Id, out var address)) continue;
                bool allowed = instruction.Operation switch {
                    ValueOperation.Let alias => alias.Value == operand,
                    ValueOperation.Access access => access.Base == operand && (dynamicCompositeIndexing
                        || access.Base.Type is not ShaderType.Pointer { Base: ShaderType.Array or ShaderType.Matrix }
                        || definitions.GetValueOrDefault(access.Index.Id) is ValueOperation.Literal),
                    ValueOperation.Member member => member.Base == operand,
                    ValueOperation.Load { MemoryAccess: null } load => load.Pointer == operand,
                    ValueOperation.Store { MemoryAccess: null } store => store.Pointer == operand && address.Path.Count == 0 && store.Value != operand,
                    _ => false
                };
                if (!allowed) eligible.Remove(address.Root.Id);
            }
        foreach (var operand in graph.Blocks.SelectMany(b => b.Terminator!.Operands.Concat(b.Terminator.Edges.SelectMany(e => e.Arguments))))
            if (addresses.TryGetValue(operand.Id, out var address)) eligible.Remove(address.Root.Id);
        if (eligible.Count == 0) return;
        foreach (var block in graph.Blocks) {
            var original = block.Instructions.ToArray(); block.Instructions.Clear();
            foreach (var instruction in original) {
                if (instruction.Operation is ValueOperation.Load load && addresses.TryGetValue(load.Pointer.Id, out var address)
                    && eligible.Contains(address.Root.Id)) {
                    if (address.Path.Count == 0) {
                        block.Instructions.Add(instruction with { Operation = load with { Pointer = address.Root } }); continue;
                    }
                    var value = graph.Value(((ShaderType.Pointer)address.Root.Type).Base);
                    block.Instructions.Add(instruction with { Result = value, Operation = new ValueOperation.Load(address.Root) });
                    for (int index = 0; index < address.Path.Count; index++) {
                        var step = address.Path[index];
                        var result = index == address.Path.Count - 1 ? instruction.Result!.Value
                            : graph.Value(((ShaderType.Pointer)step.Result!.Value.Type).Base);
                        var operation = step.Operation switch {
                            ValueOperation.Access access => (ValueOperation)new ValueOperation.Access(value, access.Index),
                            ValueOperation.Member member => new ValueOperation.Member(value, member.Name),
                            _ => throw new InvalidOperationException("Unexpected aggregate projection.")
                        };
                        block.Instructions.Add(instruction with { Result = result, Operation = operation }); value = result;
                    }
                    continue;
                }
                if (instruction.Operation is ValueOperation.Store store && addresses.TryGetValue(store.Pointer.Id, out var target)
                    && eligible.Contains(target.Root.Id)) {
                    block.Instructions.Add(instruction with { Operation = store with { Pointer = target.Root } }); continue;
                }
                if (instruction.Result is { } alias && addresses.TryGetValue(alias.Id, out var projection)
                    && eligible.Contains(projection.Root.Id) && alias.Id != projection.Root.Id) continue;
                block.Instructions.Add(instruction);
            }
        }
    }
}

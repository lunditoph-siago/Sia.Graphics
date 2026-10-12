using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Native pointer types omit access permissions; joins retain only permissions every incoming address has.</summary>
internal static class PointerAccessInference
{
    // The frontend owns this graph. Permission narrowing does not change address
    // identity, topology, memory effects or the native pointee/storage-class type.
    internal static void Run(ControlFlowFunction graph)
    {
        var values = graph.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)))
            .Where(v => v.Type is ShaderType.Pointer).ToDictionary(v => v.Id, v => v);
        var access = values.ToDictionary(p => p.Key, p => ((ShaderType.Pointer)p.Value.Type).Access);
        var blocks = graph.Blocks.ToDictionary(b => b.Id);
        bool changed;
        do {
            changed = false;
            void Narrow(SsaValue value, StorageAccess incoming) {
                if (!access.TryGetValue(value.Id, out var before)) return;
                var after = before & incoming; changed |= after != before; access[value.Id] = after;
            }
            foreach (var block in graph.Blocks) {
                foreach (var instruction in block.Instructions) {
                    if (instruction.Result is not { Type: ShaderType.Pointer } result) continue;
                    SsaValue? source = instruction.Operation switch {
                        ValueOperation.Let l => l.Value, ValueOperation.Access a => a.Base,
                        ValueOperation.Member m => m.Base, ValueOperation.Binary { Operator: "+" } b => b.Left, _ => null
                    };
                    if (source is { } pointer) Narrow(result, access[pointer.Id]);
                }
                foreach (var edge in block.Terminator!.Edges)
                    for (int i = 0; i < edge.Arguments.Count; i++)
                        if (edge.Arguments[i].Type is ShaderType.Pointer) Narrow(blocks[edge.Target].Parameters[i], access[edge.Arguments[i].Id]);
            }
        } while (changed);
        SsaValue Value(SsaValue value) => value.Type is ShaderType.Pointer pointer ? value with { Type = pointer with { Access = access[value.Id] } } : value;
        foreach (var block in graph.Blocks) {
            for (int i = 0; i < block.Parameters.Count; i++) block.Parameters[i] = Value(block.Parameters[i]);
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                block.Instructions[i] = instruction with { Result = instruction.Result is { } result ? Value(result) : null, Operation = instruction.Operation.Map(Value) };
            }
            block.Terminator = block.Terminator!.Map(Value);
            foreach (var edge in block.Terminator.Edges)
                for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Value(edge.Arguments[i]);
        }
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed partial class SpirvMemoryAccessLowering
{
    // The caller owns this graph. Only instruction metadata changes: SSA values,
    // evaluation order, block parameters and executable edges remain intact.
    internal static void Run(ControlFlowFunction graph, Module module)
        => new SpirvMemoryAccessLowering(module).Graph(graph);

    private void Graph(ControlFlowFunction graph)
    {
        var pointers = graph.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions
            .Where(i => i.Result is not null).Select(i => i.Result!.Value)))
            .Where(v => v.Type is ShaderType.Pointer).ToDictionary(v => v.Id, _ => MemoryDecorations.None);
        var arguments = graph.Signature.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        MemoryDecorations Inherited(SsaValue pointer) => pointers.GetValueOrDefault(pointer.Id);
        MemoryDecorations Unknown(SsaValue pointer) => pointer.Type is ShaderType.Pointer { Space: AddressSpace.Storage }
            ? sharedMemory : MemoryDecorations.None;
        bool Add(SsaValue pointer, MemoryDecorations flags) {
            if (pointer.Type is not ShaderType.Pointer { Space: not AddressSpace.Function }) return false;
            flags |= Inherited(pointer);
            if (flags == Inherited(pointer)) return false;
            pointers[pointer.Id] = flags; return true;
        }
        // Propagate only address-path decorations. Whole aggregate type requirements
        // belong at the access site; adding them at the root would qualify siblings.
        bool changed;
        do {
            changed = false;
            foreach (var block in graph.Blocks) {
                foreach (var edge in block.Terminator!.Edges) {
                    var target = graph.Blocks.Single(b => b.Id == edge.Target);
                    for (int i = 0; i < target.Parameters.Count; i++)
                        changed |= Add(target.Parameters[i], Inherited(edge.Arguments[i]));
                }
                foreach (var instruction in block.Instructions) {
                    if (instruction.Result is not { Type: ShaderType.Pointer } result) continue;
                    var flags = instruction.Operation switch {
                        ValueOperation.Symbol s when !arguments.Contains(s.Name) && globals.TryGetValue(s.Name, out var global) => global.MemoryDecorations,
                        ValueOperation.Let l => Inherited(l.Value),
                        ValueOperation.Access a => Inherited(a.Base),
                        ValueOperation.Member m => Inherited(m.Base) | (((m.Base.Type as ShaderType.Pointer)?.Base as ShaderType.Structure)?
                            .Members.FirstOrDefault(member => member.Name == m.Name)?.MemoryDecorations ?? MemoryDecorations.None),
                        ValueOperation.Swizzle s => Inherited(s.Vector),
                        ValueOperation.Select s => Inherited(s.Accept) | Inherited(s.Reject),
                        _ => Unknown(result)
                    };
                    changed |= Add(result, flags);
                }
            }
        } while (changed);
        MemoryDecorations Requirements(SsaValue pointer) => pointer.Type is ShaderType.Pointer { Space: AddressSpace.Function }
            ? MemoryDecorations.None : Inherited(pointer) | ShaderMemoryRequirements.TypeMemory(pointer.Type);
        AddressSpace Space(SsaValue pointer) => ((ShaderType.Pointer)pointer.Type).Space;
        SpirvMemoryAccess? Access(SsaValue pointer, SpirvMemoryAccess? memory, bool load = true)
            => ShaderMemoryRequirements.Decorate(memory, Requirements(pointer), Space(pointer), vulkan, load);
        ValueOperation.Builtin Builtin(ValueOperation.Builtin call) {
            var result = call;
            bool ordinaryAtomic = call.MemoryAccess is not null && call.Function is "atomicLoad" or "atomicStore";
            bool load = call.Function is "coopLoad" or "coopLoadT" or "workgroupUniformLoad" or "atomicLoad";
            bool store = call.Function is "coopStore" or "coopStoreT" or "atomicStore";
            if (call.Arguments.Count != 0 && (call.Function == "workgroupUniformLoad"
                || call.Function.StartsWith("coop", StringComparison.Ordinal) && (load || store) || ordinaryAtomic)) {
                var pointer = call.Arguments[call.Function is "coopStore" or "coopStoreT" ? 1 : 0];
                if (call.Function != "workgroupUniformLoad" || pointer.Type is not ShaderType.Pointer { Base: ShaderType.Atomic })
                    result = result with { MemoryAccess = Access(pointer, call.MemoryAccess, load) };
            }
            if (call.Arguments.Count != 0 && !ordinaryAtomic && (call.Function.StartsWith("atomic", StringComparison.Ordinal)
                || call.Function == "spirvAtomicCompareExchange" || call.Function == "workgroupUniformLoad"
                    && call.Arguments[0].Type is ShaderType.Pointer { Base: ShaderType.Atomic })) {
                var pointer = call.Arguments[0];
                if (vulkan && (Requirements(pointer) & MemoryDecorations.Volatile) != 0) {
                    var memory = call.AtomicMemory ?? new(Space(pointer) is AddressSpace.Workgroup or AddressSpace.TaskPayload
                        ? 2u : input.VulkanMemoryModel ? 5u : 1u, 0);
                    bool compare = call.Function is "atomicCompareExchangeWeak" or "spirvAtomicCompareExchange";
                    result = result with { AtomicMemory = memory with { Semantics = memory.Semantics | 32768,
                        UnequalSemantics = compare || memory.UnequalSemantics is not null
                            ? (memory.UnequalSemantics ?? memory.Semantics) | 32768 : null } };
                }
            }
            return result;
        }
        foreach (var block in graph.Blocks)
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                var operation = instruction.Operation switch {
                    ValueOperation.Load l => l with { MemoryAccess = Access(l.Pointer, l.MemoryAccess) },
                    ValueOperation.Store s => s with { MemoryAccess = Access(s.Pointer, s.MemoryAccess, false) },
                    ValueOperation.Builtin b => Builtin(b), _ => instruction.Operation
                };
                if (operation != instruction.Operation) block.Instructions[i] = instruction with { Operation = operation };
            }
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed partial class SpirvSynchronizationLowering
{
    // Operands are already evaluated and captured in SSA. Expand at their actual
    // use without fresh source names, rebuilding regions, or changing CFG edges.
    internal static void Run(ControlFlowFunction graph, Module module)
    {
        foreach (var block in graph.Blocks) {
            var output = new List<ControlFlowInstruction>();
            foreach (var instruction in block.Instructions) {
                ValueOperation operation = instruction.Operation;
                if (operation is ValueOperation.Barrier barrier)
                    operation = barrier with { NativeMemory = barrier.NativeMemory
                        ?? BarrierMemory(barrier.Storage, barrier.Workgroup, barrier.Texture, barrier.Subgroup, barrier.Control) };
                else if (operation is ValueOperation.Builtin builtin) {
                    if (BarrierName(builtin.Function)) {
                        bool storage = builtin.Function == "storageBarrier", workgroup = builtin.Function == "workgroupBarrier",
                            texture = builtin.Function == "textureBarrier", subgroup = builtin.Function == "subgroupBarrier";
                        operation = new ValueOperation.Barrier(true, storage, workgroup, texture, subgroup,
                            BarrierMemory(storage, workgroup, texture, subgroup, true));
                    }
                    else {
                        var atomic = DefaultAtomic(builtin.Function, builtin.Arguments.Count == 0 ? null : builtin.Arguments[0].Type,
                            builtin.AtomicMemory, builtin.MemoryAccess, module.VulkanMemoryModel);
                        if (builtin.Function == "workgroupUniformLoad") {
                            var pointer = builtin.Arguments.Single();
                            var sync = instruction with { Result = null, Operation = new ValueOperation.Barrier(true, false, true, false, false,
                                BarrierMemory(false, true, false, false, true)) };
                            output.Add(sync);
                            operation = pointer.Type is ShaderType.Pointer { Base: ShaderType.Atomic }
                                ? new ValueOperation.Builtin("atomicLoad", [pointer], builtin.ReturnType, atomic)
                                : new ValueOperation.Load(pointer, builtin.MemoryAccess);
                            output.Add(instruction with { Operation = operation }); output.Add(sync); continue;
                        }
                        operation = builtin with { AtomicMemory = atomic };
                    }
                }
                output.Add(operation == instruction.Operation ? instruction : instruction with { Operation = operation });
            }
            block.Instructions.Clear(); block.Instructions.AddRange(output);
        }
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed partial class SpirvWorkgroupAccessLowering
{
    // Mutate an owned graph. Declarations/signatures are mapped by the caller;
    // SSA IDs, captured addresses, edge objects and native access operands remain.
    internal static void Run(ControlFlowFunction graph, SpirvPhysicalLayout layout)
    {
        var pass = new SpirvWorkgroupAccessLowering(layout);
        SsaValue Map(SsaValue value) => value with { Type = pass.PointerType(value.Type) };
        foreach (var block in graph.Blocks) {
            for (int i = 0; i < block.Parameters.Count; i++) block.Parameters[i] = Map(block.Parameters[i]);
            block.Terminator = block.Terminator!.Map(Map);
            foreach (var edge in block.Terminator.Edges)
                for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Map(edge.Arguments[i]);
            var output = new List<ControlFlowInstruction>();
            SsaValue Convert(SsaValue value, ShaderType logical, bool toPhysical, ControlFlowInstruction origin, SsaValue? result = null) {
                if (pass.Physical(logical) == logical) return value;
                if (!layout.WorkgroupConversions.TryGetValue((logical, toPhysical), out var helper))
                    throw new ShaderException(DiagnosticStage.SpirvWrite, "Workgroup access needs a prepared value conversion.", origin.Span);
                var target = result ?? graph.Value(helper.ReturnType);
                output.Add(origin with { Result = target, Operation = new ValueOperation.Call(helper.Name, [value], helper.ReturnType) });
                return target;
            }
            foreach (var instruction in block.Instructions) {
                var operation = instruction.Operation.Map(Map);
                SsaValue? result = instruction.Result is { } old ? Map(old) : null;
                if (operation is ValueOperation.Load { Pointer.Type: ShaderType.Pointer { Space: AddressSpace.Workgroup } pointer } load
                    && instruction.Result is { } logical && pointer.Base != logical.Type) {
                    var physical = graph.Value(pointer.Base);
                    output.Add(instruction with { Result = physical, Operation = load });
                    Convert(physical, logical.Type, false, instruction, logical); continue;
                }
                if (operation is ValueOperation.Store { Pointer.Type: ShaderType.Pointer { Space: AddressSpace.Workgroup } } store)
                    operation = store with { Value = Convert(store.Value, store.Value.Type, true, instruction) };
                if (operation is ValueOperation.Builtin builtin) {
                    if (builtin.Function == "workgroupUniformLoad" && builtin.Arguments.Single().Type is ShaderType.Pointer { Space: AddressSpace.Workgroup } pointerType
                        && instruction.Result is { } logicalResult && pointerType.Base != logicalResult.Type) {
                        var physical = graph.Value(pointerType.Base);
                        output.Add(instruction with { Result = physical, Operation = builtin with { ReturnType = pointerType.Base } });
                        Convert(physical, logicalResult.Type, false, instruction, logicalResult); continue;
                    }
                    operation = builtin with { ReturnType = pass.PointerType(builtin.ReturnType) };
                }
                else if (operation is ValueOperation.Call call) operation = call with { ReturnType = pass.PointerType(call.ReturnType) };
                output.Add(instruction with { Result = result, Operation = operation });
            }
            block.Instructions.Clear(); block.Instructions.AddRange(output);
        }
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private bool CanHandOffToWgsl(CanonicalModule canonical)
        {
            var declarations = canonical.Declarations;
            return !variablePointers && !pointerSelection && !declarations.VulkanMemoryModel
                && canonical.DeferredFunctions.Count == 0
                && !MatrixStorageLowering.HasLayout(declarations)
                && declarations.Globals.All(static global => IsData(global.Type))
                && declarations.Constants.All(static constant => !constant.IsOverride && !constant.IsSpecialization && IsData(constant.Type))
                && declarations.Functions.All(function =>
                    function.Stage is null or ShaderStage.Compute
                    && (function.ReturnType is ShaderType.Void || IsValue(function.ReturnType))
                    && function.Arguments.All(static argument => IsValue(argument.Type))
                    && canonical.Functions.TryGetValue(function.Name, out var graph)
                    && graph.Blocks is [{ Parameters.Count: 0, Terminator: ControlFlowTerminator.Return } block]
                    && block.Instructions.All(CanHandOffInstruction));
        }

        private static bool IsData(ShaderType type) => type switch {
            ShaderType.Scalar { Kind: ScalarKind.Bool, Width: 1 } => true,
            ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint or ScalarKind.Float, Width: 4 } => true,
            ShaderType.Vector vector => IsData(vector.Component),
            ShaderType.Array { OverrideLength: null } array => IsData(array.Element),
            ShaderType.Structure structure => structure.Members.All(static member => IsData(member.Type)),
            _ => false
        };

        private static bool IsValue(ShaderType type) => CanonicalTypes.Data(type) && IsData(type);

        private static bool CanHandOffInstruction(ControlFlowInstruction instruction)
            => instruction.Operation switch {
                ValueOperation.Symbol or ValueOperation.Access or ValueOperation.Member
                    => instruction.Result is { } result
                        && (result.Type is ShaderType.Pointer pointer ? IsData(pointer.Base) : IsValue(result.Type)),
                ValueOperation.Literal or ValueOperation.Let or ValueOperation.Unary or ValueOperation.Binary
                    or ValueOperation.Convert or ValueOperation.Construct or ValueOperation.Select or ValueOperation.Swizzle
                    => instruction.Result is { } result && IsValue(result.Type)
                        && instruction.Operation.Operands.All(static operand => IsValue(operand.Type)),
                ValueOperation.Load { MemoryAccess: null }
                    => instruction.Result is { } result && IsValue(result.Type),
                ValueOperation.Store { MemoryAccess: null } store => IsValue(store.Value.Type),
                ValueOperation.Call { AtomicMemory: null, MemoryAccess: null } call
                    => call.Arguments.All(static argument => IsValue(argument.Type))
                        && (call.ReturnType is ShaderType.Void || IsValue(call.ReturnType)),
                _ => false
            };
    }
}

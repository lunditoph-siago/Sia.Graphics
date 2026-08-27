using System.Reflection.Emit;
using Sia.Spirv;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;

namespace Sia.Spirv.Compiler.Analysis;

internal static class SpirvLegalityAnalyzer
{
    public static void Analyze(
        string method,
        ShaderCilView view,
        ICollection<SpirvDiagnostic> diagnostics)
    {
        foreach (var block in view.Graph.Blocks) {
            for (var index = 0; index < block.Instructions.Count; index++) {
                var instruction = block.Instructions[index];
                if (instruction.OpCode == OpCodes.Newarr || instruction.OpCode == OpCodes.Box) {
                    Add(
                        diagnostics,
                        SpirvDiagnosticIds.ManagedHeapAllocation,
                        "Managed heap allocation is not supported inside a SPIR-V kernel.",
                        method,
                        instruction.Offset);
                }
                else if (instruction.OpCode == OpCodes.Newobj &&
                      !IsRecognizedValueTypeConstructor(view, block, index)) {
                    Add(
                        diagnostics,
                        SpirvDiagnosticIds.ManagedHeapAllocation,
                        "Managed heap allocation is not supported inside a SPIR-V kernel.",
                        method,
                        instruction.Offset);
                }
                else if (instruction.OpCode is var exceptionOpCode &&
                      (exceptionOpCode == OpCodes.Throw ||
                       exceptionOpCode == OpCodes.Rethrow ||
                       exceptionOpCode == OpCodes.Leave ||
                       exceptionOpCode == OpCodes.Leave_S ||
                       exceptionOpCode == OpCodes.Endfinally ||
                       exceptionOpCode == OpCodes.Endfilter)) {
                    Add(
                        diagnostics,
                        SpirvDiagnosticIds.ExceptionHandling,
                        "Exception handling is not supported inside a SPIR-V kernel.",
                        method,
                        instruction.Offset);
                }
                else if (instruction.OpCode == OpCodes.Callvirt &&
                      !IsProvablyNonVirtualCall(view, block, index)) {
                    Add(
                        diagnostics,
                        SpirvDiagnosticIds.DynamicDispatch,
                        "Virtual and interface dispatch are not supported inside a SPIR-V kernel.",
                        method,
                        instruction.Offset);
                }
                else if (instruction.OpCode == OpCodes.Ldstr) {
                    Add(
                        diagnostics,
                        SpirvDiagnosticIds.UnsupportedManagedValue,
                        "Managed strings are not supported inside a SPIR-V kernel.",
                        method,
                        instruction.Offset);
                }
            }
        }
    }

    private static bool IsProvablyNonVirtualCall(ShaderCilView view, CilBasicBlock block, int index)
    {
        var call = view.ResolveCall(block, index);
        return call.Intrinsic is not null || call.DeclaringType == "Sia.Spirv.UInt3";
    }

    private static bool IsRecognizedValueTypeConstructor(ShaderCilView view, CilBasicBlock block, int index)
    {
        var call = view.ResolveCall(block, index);
        return call.Intrinsic == IntrinsicKind.MathConstruct;
    }

    private static void Add(
        ICollection<SpirvDiagnostic> diagnostics,
        string id,
        string message,
        string method,
        int offset) =>
        diagnostics.Add(new SpirvDiagnostic(
            id,
            SpirvDiagnosticSeverity.Error,
            message,
            method,
            offset));
}

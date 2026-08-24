using System.Reflection.Emit;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;

namespace Sia.Spirv.Compiler.Analysis;

internal static class SpirvLegalityAnalyzer
{
    public static void Analyze(
        string method,
        CilControlFlowGraph graph,
        ICollection<SpirvDiagnostic> diagnostics)
    {
        foreach (var instruction in graph.Blocks.SelectMany(static block => block.Instructions)) {
            if (instruction.OpCode is var opCode &&
                (opCode == OpCodes.Newobj || opCode == OpCodes.Newarr || opCode == OpCodes.Box)) {
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
            else if (instruction.OpCode == OpCodes.Callvirt) {
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

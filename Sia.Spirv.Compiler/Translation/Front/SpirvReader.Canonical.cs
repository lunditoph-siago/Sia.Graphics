using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    // Family-by-family migration. Other native forms retain the existing normalizer
    // with an explicit deferral; eligibility never turns a parse error into fallback.
    private static bool CanReadCanonicalFlow(SpirvBinary binary, out string? deferred)
    {
        deferred = null;
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands.Length == 3)
            .Select(i => i.Operands[0]).ToHashSet();
        var specializations = binary.Instructions.Where(i => (Op)i.Opcode is Op.SpecConstant or Op.SpecConstantOp
            && i.Operands.Length >= 2).Select(i => i.Operands[1]).ToHashSet();
        if (!binary.Instructions.Any(i => (Op)i.Opcode == Op.EntryPoint))
            deferred = "native libraries still require frontend convergence";
        else if (binary.Instructions.Any(i => (Op)i.Opcode == Op.TypeArray && i.Operands.Length == 3
            && specializations.Contains(i.Operands[2])))
            deferred = "native specialization-sized arrays still require canonical type migration";
        else if (binary.Instructions.Any(i => (Op)i.Opcode == Op.TypePointer && i.Operands.Length == 3
            && pointers.Contains(i.Operands[2]) && i.Operands[1] is not (6 or 7)))
            deferred = "native pointer slots outside Function/Private memory still require binary provenance normalization";
        else if (binary.Instructions.Any(i => (Op)i.Opcode is Op.TypeAccelerationStructureKHR
            or Op.TypeRayQueryKHR or Op.TypeCooperativeMatrixKHR))
            deferred = "native opaque types still require frontend migration";
        else {
            var structures = binary.Instructions.Where(i => (Op)i.Opcode == Op.Decorate && i.Operands.Length >= 2 && i.Operands[1] is 2 or 3)
                .Select(i => i.Operands[0]).ToHashSet();
            if (binary.Instructions.Any(i => (Op)i.Opcode is Op.TypeArray or Op.TypeRuntimeArray && i.Operands.Length >= 2 && structures.Contains(i.Operands[1])))
                deferred = "native descriptor arrays still require descriptor identity migration";
        }
        return deferred is null;
    }
}

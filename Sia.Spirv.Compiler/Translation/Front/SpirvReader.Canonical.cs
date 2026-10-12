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
            && pointers.Contains(i.Operands[2]) && i.Operands[1] != 7))
            deferred = "native pointer slots in Private/global memory still require binary provenance normalization";
        else if (binary.Instructions.Any(i => (Op)i.Opcode is Op.ConstantNull or Op.Undef && i.Operands.Length >= 2 && pointers.Contains(i.Operands[0])))
            deferred = "native pointer null/undefined still requires binary provenance normalization";
        else if (binary.Instructions.Any(i => (Op)i.Opcode is Op.EmitMeshTasksEXT))
            deferred = "native task terminators still require canonical migration";
        else if (binary.Instructions.Any(i => (Op)i.Opcode is Op.PtrAccessChain or Op.PtrEqual or Op.PtrNotEqual or Op.PtrDiff))
            deferred = "native pointer arithmetic/comparison still requires binary provenance normalization";
        else if (binary.Instructions.Any(i => (Op)i.Opcode is >= Op.AtomicLoad and <= Op.AtomicXor or Op.AtomicFAddEXT))
            deferred = "native atomics still require typed provenance migration";
        else if (binary.Instructions.Any(i => (Op)i.Opcode is Op.TypeImage or Op.TypeSampler or Op.TypeSampledImage or Op.TypeAccelerationStructureKHR
            or Op.TypeRayQueryKHR or Op.TypeCooperativeMatrixKHR || (Op)i.Opcode is Op.MemberDecorate && i.Operands.Length >= 3 && i.Operands[2] is 4 or 5 or 7))
            deferred = "native opaque or physical matrix types still require frontend migration";
        else {
            var structures = binary.Instructions.Where(i => (Op)i.Opcode == Op.Decorate && i.Operands.Length >= 2 && i.Operands[1] is 2 or 3)
                .Select(i => i.Operands[0]).ToHashSet();
            if (binary.Instructions.Any(i => (Op)i.Opcode is Op.TypeArray or Op.TypeRuntimeArray && i.Operands.Length >= 2 && structures.Contains(i.Operands[1])))
                deferred = "native descriptor arrays still require descriptor identity migration";
        }
        return deferred is null;
    }
}

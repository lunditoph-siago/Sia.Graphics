using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private Expression NativePointerComparison()
        {
            if (current.Operands.Length != 4) throw Error("Invalid pointer comparison operands.");
            var a = current.Operands; bool difference = (Op)current.Opcode == Op.PtrDiff;
            if (binary.Version < 0x10400) throw Error("Pointer comparisons require SPIR-V 1.4 or later.");
            var result = Type(a[0]);
            if (difference ? result is not ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint } : result != ShaderType.Bool)
                throw Error(difference ? "Pointer difference result must be an integer scalar." : "Pointer comparison result must be a Boolean scalar.");
            if (!valueTypeIds.TryGetValue(a[2], out uint left) || !valueTypeIds.TryGetValue(a[3], out uint right)
                || Type(left) is not ShaderType.Pointer pointer || Type(right) is not ShaderType.Pointer)
                throw Error("Pointer comparison references an undefined pointer.");
            if (left != right) throw Error("Pointer comparison operand type mismatch.");
            if (pointer.Space is not (AddressSpace.Storage or AddressSpace.Workgroup) || pointer.Space == AddressSpace.Workgroup && !fullVariablePointers)
                throw Error("Pointer comparisons require storage memory or full VariablePointers Workgroup memory.");
            if (difference && pointer.Space == AddressSpace.Storage && Decoration(left, 6) is not > 0)
                throw Error("Storage pointer difference requires an ArrayStride on its pointer type.");
            return new Expression.Binary(difference ? "-" : (Op)current.Opcode == Op.PtrEqual ? "==" : "!=", Value(a[2]), Value(a[3]), result) {
                Span = new(current.WordOffset * 4, current.WordCount * 4)
            };
        }
    }
}

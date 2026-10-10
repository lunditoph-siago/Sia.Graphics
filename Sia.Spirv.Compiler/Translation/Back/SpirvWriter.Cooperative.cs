using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private sealed partial class FunctionEmitter
        {
            private uint Cooperative(Expression.Call call)
            {
                if (call.Function == "coopMultiplyAdd") return Result(Op.CooperativeMatrixMulAddKHR, call.Type, call.Arguments.Select(Value).ToArray());
                bool load = call.Function is "coopLoad" or "coopLoadT";
                if (call.Arguments[load ? 0 : 1].Type is not ShaderType.Pointer { Space: AddressSpace.Storage or AddressSpace.Workgroup })
                    throw owner.Error("Vulkan cooperative memory operations require storage or workgroup memory.", call.Span);
                uint[] args = call.Arguments.Select(Value).ToArray();
                // The pinned WGSL extension uses T for row-major memory.
                uint layout = owner.Constant(Expression.U32(call.Function.EndsWith('T') ? 0u : 1u));
                var memory = call.MemoryAccess;
                if (load) return Result(Op.CooperativeMatrixLoadKHR, call.Type, new[] { args[0], layout, args[1] }.Concat(MemoryOperands(memory)).ToArray());
                Add(Op.CooperativeMatrixStoreKHR, new[] { args[1], args[0], layout, args[2] }.Concat(MemoryOperands(memory)).ToArray()); return 0;
            }
        }
    }
}

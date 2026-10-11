using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private static MemoryDecorations TypeMemory(ShaderType type) => ShaderMemoryRequirements.TypeMemory(type);
        private uint[] MemoryOperands(SpirvMemoryAccess? memory)
        {
            if (memory is null) return [];
            var args = new List<uint> { memory.Flags };
            if (memory.Alignment is uint alignment) args.Add(alignment);
            foreach (uint scope in new[] { memory.AvailableScope, memory.VisibleScope }.OfType<uint>())
            {
                if (scope == 1) usesDeviceScope = true;
                args.Add(Constant(Expression.U32(scope)));
            }
            return args.ToArray();
        }
        private static bool UsesCooperativeMemoryModel(Module module) => ShaderMemoryRequirements.UsesCooperativeMemoryModel(module);
        private sealed partial class FunctionEmitter
        {
            private uint[] MemoryOperands(SpirvMemoryAccess? memory)
                => owner.MemoryOperands(memory);
            private uint MemoryLoad(uint pointer, ShaderType type, SpirvMemoryAccess? memory) =>
                Result(Op.Load, type, new[] { pointer }.Concat(MemoryOperands(memory)).ToArray());
            private void MemoryStore(uint pointer, uint value, SpirvMemoryAccess? memory) =>
                Add(Op.Store, new[] { pointer, value }.Concat(MemoryOperands(memory)).ToArray());
            private uint MemoryLoad(Expression pointer, ShaderType type, SpirvMemoryAccess? memory = null) =>
                MemoryLoad(Place(pointer), type, memory);
        }
    }
}

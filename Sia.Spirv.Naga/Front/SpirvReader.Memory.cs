using Sia.Spirv.Naga.IR;

namespace Sia.Spirv.Naga.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private SpirvMemoryAccess? MemoryAccess(ref int index)
        {
            var operands = current.Operands;
            if (index == operands.Length) return null;
            int cursor = index; uint flags = operands[cursor++];
            if ((flags & ~63u) != 0) throw Error("Unsupported native per-access memory operand flags.");
            uint Next() { if (cursor >= operands.Length) throw Error("Missing per-access memory operand."); return operands[cursor++]; }
            uint? alignment = (flags & 2) != 0 ? Next() : null;
            uint? available = (flags & 8) != 0 ? ConstantUint(Next()) : null;
            uint? visible = (flags & 16) != 0 ? ConstantUint(Next()) : null;
            index = cursor; return new(flags, alignment, available, visible);
        }
        private SpirvMemoryAccess? MemoryAccess(int required)
        {
            Count(required); int index = required; var memory = MemoryAccess(ref index);
            if (index != current.Operands.Length) throw Error("Unexpected per-access memory operands.");
            return memory;
        }
        private (SpirvMemoryAccess? Target, SpirvMemoryAccess? Source) CopyMemoryAccess()
        {
            Count(2); int index = 2; var target = MemoryAccess(ref index);
            if (index == current.Operands.Length)
            {
                // One mask applies to both pointers. Availability is after the
                // write and visibility before the read, not to the other half.
                return (target is null ? null : target with { Flags = target.Flags & ~16u, VisibleScope = null },
                    target is null ? null : target with { Flags = target.Flags & ~8u, AvailableScope = null });
            }
            if (binary.Version < 0x10400) throw Error("Two CopyMemory access masks require SPIR-V 1.4 or later.");
            var source = MemoryAccess(ref index);
            if (index != current.Operands.Length) throw Error("Unexpected CopyMemory access operands.");
            return (target, source);
        }
        private SpirvMemoryAccess? FunctionMemoryAccess(Expression place, SpirvMemoryAccess? memory)
        {
            if (place.Type is not ShaderType.Pointer { Space: AddressSpace.Function }) return memory;
            MemoryDecorations TypeMemory(ShaderType type) => type switch
            {
                ShaderType.Pointer p => TypeMemory(p.Base), ShaderType.Array a => TypeMemory(a.Element),
                ShaderType.Structure s => s.Members.Aggregate(MemoryDecorations.None, (flags, m) => flags | m.MemoryDecorations | TypeMemory(m.Type)),
                _ => MemoryDecorations.None
            };
            MemoryDecorations Path(Expression e) => e switch
            {
                Expression.Member m => Path(m.Base) | (((m.Base.Type is ShaderType.Pointer p ? p.Base : m.Base.Type) as ShaderType.Structure)
                    ?.Members.FirstOrDefault(f => f.Name == m.Name)?.MemoryDecorations ?? MemoryDecorations.None),
                Expression.Access a => Path(a.Base), Expression.Unary { Operator: "&" or "*" } u => Path(u.Operand), _ => MemoryDecorations.None
            };
            // Capture only original function-memory operations. Newly created
            // SSA registers/snapshots must not inherit this type's decorations.
            if (((Path(place) | TypeMemory(place.Type)) & MemoryDecorations.Volatile) != 0)
                return (memory ?? new(0)) with { Flags = (memory?.Flags ?? 0) | 1 };
            return memory;
        }
        private Expression MemoryLoad(Expression place, ShaderType type, SpirvMemoryAccess? memory)
        {
            memory = FunctionMemoryAccess(place, memory);
            return place.Type is ShaderType.Pointer { Base: ShaderType.Atomic }
                ? ((Expression.Call)AtomicCall("atomicLoad", place, [], type)) with { MemoryAccess = memory ?? new(0) }
                : new Expression.Load(place) { MemoryAccess = memory };
        }
        private Statement MemoryStore(Expression place, Expression value, SpirvMemoryAccess? memory)
        {
            memory = FunctionMemoryAccess(place, memory);
            return place.Type is ShaderType.Pointer { Base: ShaderType.Atomic }
                ? new Statement.Evaluate(((Expression.Call)AtomicCall("atomicStore", place, [value], new ShaderType.Void())) with { MemoryAccess = memory ?? new(0) })
                : new Statement.Store(place, value) { MemoryAccess = memory };
        }
    }
}

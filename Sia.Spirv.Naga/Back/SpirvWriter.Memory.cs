using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private static MemoryDecorations TypeMemory(ShaderType type) => type switch
        {
            ShaderType.Pointer p => TypeMemory(p.Base), ShaderType.Array a => TypeMemory(a.Element),
            ShaderType.BindingArray a => TypeMemory(a.Element),
            ShaderType.Structure s => s.Members.Aggregate(MemoryDecorations.None, (flags, m) => flags | m.MemoryDecorations | TypeMemory(m.Type)),
            _ => MemoryDecorations.None
        };
        private readonly MemoryDecorations sharedMemory = module.Globals.Where(g => g.Space == AddressSpace.Storage)
            .Aggregate(MemoryDecorations.None, (flags, g) => flags | g.MemoryDecorations | TypeMemory(g.Type));
        private readonly Dictionary<string, MemoryDecorations> globalMemory = module.Globals.ToDictionary(g => g.Name, g => g.MemoryDecorations, StringComparer.Ordinal);
        private SpirvMemoryAccess? DecoratedMemory(SpirvMemoryAccess? memory, MemoryDecorations decoration, AddressSpace space, bool load = true)
        {
            if (!UsesVulkanMemoryModel) return memory;
            // GLSL/WGSL workgroup storage is implicitly coherent. Retain that
            // ordering on every access, including generated initialization.
            if (space == AddressSpace.Workgroup) decoration |= MemoryDecorations.Coherent;
            if ((decoration & MemoryDecorations.Volatile) != 0) memory = (memory ?? new(0)) with { Flags = (memory?.Flags ?? 0) | 1 };
            if ((decoration & MemoryDecorations.Coherent) == 0 || space is not (AddressSpace.Storage or AddressSpace.Uniform or AddressSpace.Workgroup)) return memory;
            uint RequiredScope(uint? existing) => existing == 1 ? 1u : space == AddressSpace.Workgroup && existing != 5 ? 2u : 5u;
            memory ??= new(0);
            return load ? memory with { Flags = memory.Flags | 48, VisibleScope = RequiredScope(memory.VisibleScope) }
                : memory with { Flags = memory.Flags | 40, AvailableScope = RequiredScope(memory.AvailableScope) };
        }
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
        private static bool UsesCooperativeMemoryModel(Module module)
        {
            bool Type(ShaderType type) => type switch
            {
                ShaderType.CooperativeMatrix => true, ShaderType.Array a => Type(a.Element), ShaderType.Pointer p => Type(p.Base),
                ShaderType.Structure s => s.Members.Any(m => Type(m.Type)), _ => false
            };
            bool Expr(Expression e) => Type(e.Type) || e switch
            {
                Expression.Load l => Expr(l.Pointer), Expression.Unary u => Expr(u.Operand), Expression.Binary b => Expr(b.Left) || Expr(b.Right),
                Expression.Call c => c.Arguments.Any(Expr), Expression.Construct c => c.Components.Any(Expr), Expression.Convert c => Expr(c.Operand),
                Expression.Access a => Expr(a.Base) || Expr(a.Index), Expression.Member m => Expr(m.Base), Expression.Swizzle s => Expr(s.Vector),
                Expression.Select s => Expr(s.Condition) || Expr(s.Accept) || Expr(s.Reject), _ => false
            };
            bool Body(Block b) => b.Statements.Any(s => s switch
            {
                Statement.Declare d => Type(d.Type) || d.Initializer is { } e && Expr(e), Statement.Store st => Expr(st.Target) || Expr(st.Value),
                Statement.Evaluate ev => Expr(ev.Value), Statement.Nested n => Body(n.Body), Statement.If i => Expr(i.Condition) || Body(i.Accept) || Body(i.Reject),
                Statement.Loop l => Body(l.Body) || Body(l.Continuing) || l.BreakIf is { } e && Expr(e),
                Statement.Switch sw => Expr(sw.Selector) || sw.Cases.Any(c => Body(c.Body)), Statement.Return { Value: { } e } => Expr(e), _ => false
            });
            return module.Globals.Any(g => Type(g.Type)) || module.Functions.Any(f => Type(f.ReturnType) || f.Arguments.Any(a => Type(a.Type)) || Body(f.Body));
        }
        private sealed partial class FunctionEmitter
        {
            private MemoryDecorations PointerMemory(Expression pointer)
            {
                Expression root = pointer;
                while (root is Expression.Unary { Operator: "&" or "*" } or Expression.Access or Expression.Member)
                    root = root switch { Expression.Unary u => u.Operand, Expression.Access a => a.Base, Expression.Member m => m.Base, _ => root };
                // Native function-memory requirements are captured on their
                // original accesses by the reader. SSA values and aggregate
                // snapshots reuse the logical value type; its member metadata
                // must not qualify the new function temporaries we introduce.
                if (pointer.Type is ShaderType.Pointer { Space: AddressSpace.Function }
                    || root is Expression.Reference local && Lookup(local.Name) is { Place: true, Storage: 7 })
                    return MemoryDecorations.None;
                MemoryDecorations Inherited(Expression e)
                {
                    switch (e)
                    {
                        case Expression.Unary { Operator: "&" or "*" } u: return Inherited(u.Operand);
                        case Expression.Access a: return Inherited(a.Base);
                        case Expression.Member m:
                            var structure = DataType(m.Base.Type) as ShaderType.Structure;
                            return Inherited(m.Base) | (structure?.Members.FirstOrDefault(f => f.Name == m.Name)?.MemoryDecorations ?? MemoryDecorations.None);
                        case Expression.Reference r when Lookup(r.Name).MemoryRequirement is { } captured: return captured;
                        case Expression.Reference r when owner.globals.TryGetValue(r.Name, out var global) && Lookup(r.Name).Id == global.Id:
                            return owner.globalMemory.GetValueOrDefault(r.Name);
                        default:
                            // Unknown storage aliases may reach any qualified
                            // binding. Strengthen their existing accesses.
                            return e.Type is ShaderType.Pointer { Space: AddressSpace.Storage } ? owner.sharedMemory : MemoryDecorations.None;
                    }
                }
                return Inherited(pointer) | TypeMemory(pointer.Type);
            }
            private bool VolatileAccess(Expression pointer) => owner.UsesVulkanMemoryModel && (PointerMemory(pointer) & MemoryDecorations.Volatile) != 0;
            private SpirvMemoryAccess? AccessMemory(Expression pointer, SpirvMemoryAccess? memory, bool load = true)
            {
                return DecoratedMemory(memory, PointerMemory(pointer), PointerType(pointer.Type, pointer).Space, load);
            }
            private SpirvMemoryAccess? DecoratedMemory(SpirvMemoryAccess? memory, MemoryDecorations decoration, AddressSpace space, bool load = true)
                => owner.DecoratedMemory(memory, decoration, space, load);
            private uint[] MemoryOperands(SpirvMemoryAccess? memory)
                => owner.MemoryOperands(memory);
            private uint MemoryLoad(uint pointer, ShaderType type, SpirvMemoryAccess? memory) =>
                Result(Op.Load, type, new[] { pointer }.Concat(MemoryOperands(memory)).ToArray());
            private void MemoryStore(uint pointer, uint value, SpirvMemoryAccess? memory) =>
                Add(Op.Store, new[] { pointer, value }.Concat(MemoryOperands(memory)).ToArray());
            private uint MemoryLoad(Expression pointer, ShaderType type, SpirvMemoryAccess? memory = null) =>
                MemoryLoad(Place(pointer), type, AccessMemory(pointer, memory));
        }
    }
}

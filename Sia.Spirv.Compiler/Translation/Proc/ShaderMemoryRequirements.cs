using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static class ShaderMemoryRequirements
{
    internal static MemoryDecorations TypeMemory(ShaderType type) => type switch
        {
            ShaderType.Pointer p => TypeMemory(p.Base), ShaderType.Array a => TypeMemory(a.Element),
            ShaderType.BindingArray a => TypeMemory(a.Element),
            ShaderType.Structure s => s.Members.Aggregate(MemoryDecorations.None, (flags, m) => flags | m.MemoryDecorations | TypeMemory(m.Type)),
            _ => MemoryDecorations.None
        };
        internal static SpirvMemoryAccess? Decorate(SpirvMemoryAccess? memory, MemoryDecorations decoration, AddressSpace space, bool usesVulkanMemoryModel, bool load = true)
        {
            if (!usesVulkanMemoryModel) return memory;
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
        internal static bool UsesCooperativeMemoryModel(Module module)
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
}

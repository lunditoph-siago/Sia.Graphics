using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Make implicit workgroup zeroing and its collective synchronization explicit before serialization.</summary>
internal static class SpirvWorkgroupInitializationLowering
{
    internal static ShaderFunction Create(Module module, HashSet<string> names)
    {
        string Fresh(string stem) {
            string name = stem; int suffix = 0;
            while (!names.Add(name)) name = stem + "_" + ++suffix;
            return name;
        }
        bool Atomic(ShaderType type) => type switch {
            ShaderType.Atomic => true, ShaderType.Array a => Atomic(a.Element),
            ShaderType.Structure s => s.Members.Any(m => Atomic(m.Type)), _ => false
        };
        bool Qualified(ShaderType type) => type switch {
            ShaderType.Array a => Qualified(a.Element),
            ShaderType.Structure s => s.Members.Any(m => m.MemoryDecorations != MemoryDecorations.None || Qualified(m.Type)), _ => false
        };
        void Initialize(Block body, Expression place, ShaderType type, MemoryDecorations inherited)
        {
            if (!Atomic(type) && !Qualified(type) && type is not ShaderType.Array { OverrideLength: not null }) {
                body.Statements.Add(new Statement.Store(place, new Expression.Construct(type, [])) {
                    MemoryAccess = module.VulkanMemoryModel ? new(40u | ((inherited & MemoryDecorations.Volatile) != 0 ? 1u : 0u), AvailableScope: 2) : null
                });
                return;
            }
            if (type is ShaderType.Atomic atomic) {
                var address = new Expression.Unary("&", place, new ShaderType.Pointer(type, AddressSpace.Workgroup));
                body.Statements.Add(new Statement.Evaluate(new Expression.Call("atomicStore", [address, new Expression.Construct(atomic.Component, [])], new ShaderType.Void()) {
                    Binding = CallBinding.Builtin,
                    AtomicMemory = new(2, module.VulkanMemoryModel && (inherited & MemoryDecorations.Volatile) != 0 ? 32768u : 0u)
                }));
                return;
            }
            if (type is ShaderType.Array { Length: uint length } array) {
                for (uint i = 0; i < length; i++)
                    Initialize(body, new Expression.Access(place, Expression.U32(i), array.Element), array.Element, inherited);
            }
            else if (type is ShaderType.Array { OverrideLength: string pending } specialized) {
                string counter = Fresh("sia_workgroup_index");
                var index = new Expression.Reference(counter, ShaderType.U32);
                var constant = module.Constants.Single(c => c.Name == pending);
                Expression limit = new Expression.Reference(pending, constant.Type);
                if (limit.Type != ShaderType.U32) limit = new Expression.Convert(ShaderType.U32, limit);
                body.Statements.Add(new Statement.Declare(counter, ShaderType.U32, Expression.U32(0)));
                var loop = new Block(); var done = new Block(); done.Statements.Add(new Statement.Break());
                loop.Statements.Add(new Statement.If(new Expression.Binary(">=", index, limit, ShaderType.Bool), done, new Block()));
                Initialize(loop, new Expression.Access(place, index, specialized.Element), specialized.Element, inherited);
                var continuing = new Block();
                continuing.Statements.Add(new Statement.Store(index, new Expression.Binary("+", index, Expression.U32(1), ShaderType.U32)));
                body.Statements.Add(new Statement.Loop(loop, continuing));
            }
            else if (type is ShaderType.Structure structure) {
                foreach (var member in structure.Members)
                    Initialize(body, new Expression.Member(place, member.Name, member.Type), member.Type, inherited | member.MemoryDecorations);
            }
            else throw new ShaderException(DiagnosticStage.SpirvWrite, "Unsupported atomic workgroup initialization.");
        }

        var function = new ShaderFunction(Fresh("sia_spv_workgroup_initialize"));
        string localIndex = Fresh("sia_local_invocation_index");
        function.Arguments.Add(new(localIndex, ShaderType.U32));
        var initialize = new Block();
        foreach (var global in module.Globals.Where(g => g.Space == AddressSpace.Workgroup))
            Initialize(initialize, new Expression.Reference(global.Name, global.Type), global.Type, global.MemoryDecorations);
        function.Body.Statements.Add(new Statement.If(new Expression.Binary("==", new Expression.Reference(localIndex, ShaderType.U32), Expression.U32(0), ShaderType.Bool), initialize, new Block()));
        function.Body.Statements.Add(new Statement.Barrier(false, true) { NativeMemory = new(2, 0x108, 2) });
        return function;
    }
}

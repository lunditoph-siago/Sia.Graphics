using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Dictionary<ShaderType, ShaderType> workgroupTypes = [];
        private ShaderType WorkgroupType(ShaderType logical)
        {
            if (workgroupTypes.TryGetValue(logical, out var cached)) return cached;
            ShaderType physical = logical;
            if (logical is ShaderType.Array array)
            {
                ShaderType element = WorkgroupType(array.Element);
                if (element != array.Element || bufferTypes.Contains(array))
                {
                    // Distinct aggregate identity without buffer layout decorations. The
                    // stride is metadata only here; SPIR-V workgroup memory has no CPU ABI.
                    uint? identityStride = array.Stride is null ? TypeLayout.Of(array.Element).Stride : null;
                    physical = array with { Element = element, Stride = identityStride };
                }
            }
            else if (logical is ShaderType.Structure structure)
            {
                var members = structure.Members.Select(m => m with { Type = WorkgroupType(m.Type) }).ToArray();
                if (bufferTypes.Contains(structure) || members.Where((m, i) => m.Type != structure.Members[i].Type).Any())
                    physical = new ShaderType.Structure("SpirvWorkgroup_" + structure.Name, members);
            }
            workgroupTypes.Add(logical, physical); return physical;
        }

        private sealed partial class FunctionEmitter
        {
            private bool IsWorkgroupPlace(Expression expression) => expression.Type is ShaderType.Pointer { Space: AddressSpace.Workgroup } || expression switch
            {
                Expression.Reference r => Lookup(r.Name) is { Place: true, Storage: 4 },
                Expression.Access a => IsWorkgroupPlace(a.Base), Expression.Member m => IsWorkgroupPlace(m.Base),
                _ => false
            };
            private uint LoadWorkgroup(uint pointer, ShaderType logical, SpirvMemoryAccess? memory)
            {
                ShaderType physical = owner.WorkgroupType(logical);
                return ConvertWorkgroupValue(MemoryLoad(pointer, physical, memory), logical, false);
            }
            private uint ConvertWorkgroupValue(uint value, ShaderType logical, bool toPhysical)
            {
                ShaderType physical = owner.WorkgroupType(logical);
                if (logical == physical) return value;
                ShaderType from = toPhysical ? logical : physical, to = toPhysical ? physical : logical;
                if (logical is ShaderType.Array array && physical is ShaderType.Array mapped)
                {
                    if (array.Length is not uint length) throw owner.Error("Workgroup array must have a fixed length.");
                    var values = new List<uint>();
                    for (uint i = 0; i < length; i++) values.Add(ConvertWorkgroupValue(Result(Op.CompositeExtract, toPhysical ? array.Element : mapped.Element, value, i), array.Element, toPhysical));
                    return Result(Op.CompositeConstruct, to, values.ToArray());
                }
                if (logical is ShaderType.Structure structure && physical is ShaderType.Structure mappedStructure)
                {
                    var values = new List<uint>();
                    for (int i = 0; i < structure.Members.Count; i++) values.Add(ConvertWorkgroupValue(Result(Op.CompositeExtract, toPhysical ? structure.Members[i].Type : mappedStructure.Members[i].Type, value, (uint)i), structure.Members[i].Type, toPhysical));
                    return Result(Op.CompositeConstruct, to, values.ToArray());
                }
                throw owner.Error("Incompatible workgroup layout conversion.");
            }
        }
    }
}

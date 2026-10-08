using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private static bool AtomicInstruction(Op op) => op is >= Op.AtomicLoad and <= Op.AtomicXor or Op.AtomicFAddEXT;
        private readonly Dictionary<uint, (uint Type, uint Image, uint Coordinate, uint Sample)> imageTexelPointers = [];
        private readonly HashSet<uint> imagePointerTypes = [];
        private readonly HashSet<uint> imageAtomicResults = [];
        private readonly Dictionary<ShaderType, ShaderType> atomicDataTypes = [];
        private int atomicCopyCounter;

        private void UpgradeAtomicGlobals()
        {
            var pointers = new Dictionary<uint, (uint Root, uint[] Path)[]>();
            foreach (uint id in globals.Keys) pointers.Add(id, [(id, [])]);
            foreach (var i in binary.Instructions.Where(i => (Op)i.Opcode == Op.ConstantNull && types.GetValueOrDefault(i.Operands[0]) is ShaderType.Pointer))
                pointers.Add(i.Operands[1], [(0, [])]);
            foreach (var instruction in binary.Instructions.Where(i => (Op)i.Opcode == Op.ImageTexelPointer))
            {
                current = instruction; Count(5, 5); var a = instruction.Operands;
                if (!imagePointerTypes.Contains(a[0])) throw Error("Image texel pointer requires Image storage class.");
                imageTexelPointers.Add(a[1], (a[0], a[2], a[3], a[4]));
            }
            var pending = binary.Instructions.Where(i => (Op)i.Opcode is Op.AccessChain or Op.InBoundsAccessChain or Op.PtrAccessChain or Op.CopyObject
                || (Op)i.Opcode == Op.Select && types.GetValueOrDefault(i.Operands[0]) is ShaderType.Pointer).ToList();
            bool changed;
            do
            {
                changed = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var instruction = pending[i]; current = instruction; var a = instruction.Operands;
                    if (a.Length < 3) continue;
                    if ((Op)instruction.Opcode == Op.Select)
                    {
                        if (a.Length != 5) throw Error("Invalid pointer selection operands.");
                        if (!pointers.TryGetValue(a[3], out var accept) || !pointers.TryGetValue(a[4], out var reject)) continue;
                        pointers[a[1]] = accept.Concat(reject).DistinctBy(p => (p.Root, string.Join(',', p.Path))).ToArray();
                        pending.RemoveAt(i); changed = true; continue;
                    }
                    if ((Op)instruction.Opcode == Op.CopyObject && imageTexelPointers.TryGetValue(a[2], out var texel))
                    { imageTexelPointers[a[1]] = texel; pending.RemoveAt(i); changed = true; continue; }
                    if (!pointers.TryGetValue(a[2], out var source)) continue;
                    pointers[a[1]] = source.Select(p => (p.Root, p.Path.Concat((Op)instruction.Opcode is Op.CopyObject or Op.PtrAccessChain ? [] : a[3..]).ToArray())).ToArray();
                    pending.RemoveAt(i); changed = true;
                }
            } while (changed);
            foreach (var instruction in binary.Instructions.Where(i => AtomicInstruction((Op)i.Opcode)))
            {
                current = instruction; var a = instruction.Operands; Op op = (Op)instruction.Opcode;
                Count(op == Op.AtomicStore ? 4 : 5);
                uint pointer = a[op == Op.AtomicStore ? 0 : 2];
                if (imageTexelPointers.TryGetValue(pointer, out var imagePointer))
                {
                    if (op is Op.AtomicLoad or Op.AtomicStore or Op.AtomicExchange or Op.AtomicCompareExchange or Op.AtomicCompareExchangeWeak or Op.AtomicIIncrement or Op.AtomicIDecrement or Op.AtomicISub or Op.AtomicFAddEXT)
                        throw Error("Image atomic opcode has no WGSL equivalent.");
                    if (!pointers.TryGetValue(imagePointer.Image, out var imageLocations) || imageLocations.Length != 1) throw Error("Image atomic must reference a global texture or binding array.");
                    var imageLocation = imageLocations[0];
                    var imageGlobal = globals[imageLocation.Root];
                    ShaderType UpgradeImage(ShaderType type) => type switch
                    {
                        ShaderType.Image image when image.Access == StorageAccess.ReadWrite || image.Access == (StorageAccess.ReadWrite | StorageAccess.Atomic) => image with { Access = StorageAccess.ReadWrite | StorageAccess.Atomic },
                        ShaderType.BindingArray array => array with { Element = UpgradeImage(array.Element) },
                        _ => throw Error("Image atomics require read-write storage textures.")
                    };
                    var variable = imageGlobal.Variable with { Type = UpgradeImage(imageGlobal.Variable.Type) };
                    module.Globals[module.Globals.IndexOf(imageGlobal.Variable)] = variable;
                    globals[imageLocation.Root] = (variable, imageGlobal.Storage, imageGlobal.Binding);
                    values[imageLocation.Root] = new Expression.Reference(variable.Name, variable.Type);
                    imageAtomicResults.Add(a[1]);
                    continue;
                }
                if (!pointers.TryGetValue(pointer, out var locations)) throw Error("Atomic pointer must resolve to storage, workgroup or task payload memory.");
                foreach (var location in locations)
                    if (location.Root != 0) UpgradeAtomicLocation(location.Root, location.Path);
            }
        }

        private bool UpgradeAtomicLocation(uint root, uint[] path)
        {
            var global = globals[root];
            if (global.Variable.Space is not (AddressSpace.Storage or AddressSpace.Workgroup or AddressSpace.TaskPayload)) throw Error("Atomic operation uses invalid memory space.");
            ShaderType Upgrade(ShaderType type, int depth)
            {
                ShaderType Remember(ShaderType result)
                {
                    atomicDataTypes[result] = atomicDataTypes.GetValueOrDefault(type, type); return result;
                }
                if (depth == path.Length)
                    return type is ShaderType.Atomic ? type : type is ShaderType.Scalar scalar && scalar is ({ Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 4 or 8 } or { Kind: ScalarKind.Float, Width: 4 }) ? Remember(new ShaderType.Atomic(scalar)) : throw Error("Atomic operand must be i32, u32, i64, u64 or f32.");
                if (type is ShaderType.Array array)
                {
                    var element = Upgrade(array.Element, depth + 1);
                    return ReferenceEquals(element, array.Element) ? type : Remember(array with { Element = element });
                }
                if (type is ShaderType.BindingArray bindings)
                {
                    var element = Upgrade(bindings.Element, depth + 1);
                    return ReferenceEquals(element, bindings.Element) ? type : Remember(bindings with { Element = element });
                }
                if (type is ShaderType.Structure structure)
                {
                    uint index = ConstantUint(path[depth]);
                    if (index >= structure.Members.Count) throw Error("Atomic structure index is out of range.");
                    var member = structure.Members[(int)index]; var memberType = Upgrade(member.Type, depth + 1);
                    if (ReferenceEquals(memberType, member.Type)) return type;
                    var members = structure.Members.ToArray(); members[index] = member with { Type = memberType };
                    var result = new ShaderType.Structure("Atomic_" + structure.Name + "_" + module.Structures.Count, members);
                    module.Structures.Add(result); return Remember(result);
                }
                throw Error("Invalid atomic access path.");
            }
            var type = Upgrade(global.Variable.Type, 0);
            if (ReferenceEquals(type, global.Variable.Type)) return false;
            var upgraded = global.Variable with { Type = type };
            module.Globals[module.Globals.IndexOf(global.Variable)] = upgraded;
            globals[root] = (upgraded, global.Storage, global.Binding);
            values[root] = new Expression.Reference(upgraded.Name, new ShaderType.Pointer(upgraded.Type, upgraded.Space, upgraded.Access));
            return true;
        }

        private static bool AtomicData(ShaderType type) => type switch
        {
            ShaderType.Atomic => true, ShaderType.Array a => AtomicData(a.Element),
            ShaderType.Structure s => s.Members.Any(m => AtomicData(m.Type)), _ => false
        };
        private bool AtomicAggregateCopy(Block body, Expression pointer, Expression value, bool load, SpirvMemoryAccess? memoryAccess = null)
        {
            if (pointer.Type is not ShaderType.Pointer p || !AtomicData(p.Base)) return false;
            if (!load)
            {
                string name = "sia_atomic_copy_value_" + atomicCopyCounter++;
                body.Statements.Add(new Statement.Declare(name, value.Type, value, false));
                value = new Expression.Reference(name, value.Type);
            }
            void Copy(Block target, Expression memory, Expression data)
            {
                var type = ((ShaderType.Pointer)memory.Type).Base;
                if (type is ShaderType.Structure structure)
                {
                    for (int i = 0; i < structure.Members.Count; i++) Copy(target, Index(memory, Expression.U32((uint)i)), Index(data, Expression.U32((uint)i)));
                }
                else if (type is ShaderType.Array array)
                {
                    Expression length = array.Length is uint count ? Expression.U32(count)
                        : array.OverrideLength is string constant ? new Expression.Reference(constant, module.Constants.Single(c => c.Name == constant).Type)
                        : throw Error("Whole atomic-array accesses require a fixed or specialization length.");
                    if (length.Type != ShaderType.U32) length = new Expression.Convert(ShaderType.U32, length);
                    string name = "sia_atomic_copy_index_" + atomicCopyCounter++;
                    var counter = new Expression.Reference(name, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function));
                    target.Statements.Add(new Statement.Declare(name, ShaderType.U32, Expression.U32(0)));
                    var loop = new Block(); var done = new Block(); done.Statements.Add(new Statement.Break());
                    loop.Statements.Add(new Statement.If(new Expression.Binary(">=", new Expression.Load(counter), length, ShaderType.Bool), done, new()));
                    Copy(loop, Index(memory, new Expression.Load(counter)), Index(data, new Expression.Load(counter)));
                    var continuing = new Block(); continuing.Statements.Add(new Statement.Store(counter, new Expression.Binary("+", new Expression.Load(counter), Expression.U32(1), ShaderType.U32)));
                    target.Statements.Add(new Statement.Loop(loop, continuing));
                }
                else if (load) target.Statements.Add(new Statement.Store(data, MemoryLoad(memory,
                    type is ShaderType.Atomic atomic ? atomic.Component : type, memoryAccess?.Leaf())));
                else target.Statements.Add(MemoryStore(memory, data, memoryAccess?.Leaf()));
            }
            Copy(body, pointer, value); return true;
        }

        private SpirvAtomicMemory AtomicMemory(uint scopeId, uint semanticsId, uint? unequal = null) =>
            new(ConstantUint(scopeId), ConstantUint(semanticsId), unequal is uint id ? ConstantUint(id) : null);
        private Expression AtomicCall(string name, Expression place, IReadOnlyList<Expression> arguments, ShaderType result, SpirvAtomicMemory? memory = null)
        {
            if (IsNullPointer(place) && place.Type is ShaderType.Pointer { Base: ShaderType.Scalar scalar } nullPointer)
                place = new Expression.Construct(nullPointer with { Base = new ShaderType.Atomic(scalar) }, []);
            if (place.Type is not ShaderType.Pointer { Base: ShaderType.Atomic } pointer) throw Error("Atomic memory was not upgraded to an atomic type.");
            return new Expression.Call(name, new Expression[] { new Expression.Unary("&", place, pointer) }.Concat(arguments).ToArray(), result) { AtomicMemory = memory };
        }
        private Expression LowerAtomic(Op op, uint[] a)
        {
            Count(op == Op.AtomicLoad || op is Op.AtomicIIncrement or Op.AtomicIDecrement ? 5 : op is Op.AtomicCompareExchange or Op.AtomicCompareExchangeWeak ? 8 : 6,
                op == Op.AtomicLoad || op is Op.AtomicIIncrement or Op.AtomicIDecrement ? 5 : op is Op.AtomicCompareExchange or Op.AtomicCompareExchangeWeak ? 8 : 6);
            var memory = AtomicMemory(a[3], a[4]); Expression pointer = Value(a[2]); ShaderType type = Type(a[0]);
            if (type is not ShaderType.Scalar scalar) throw Error("Atomic result must be a scalar.");
            if (op == Op.AtomicFAddEXT ? scalar != ShaderType.F32 : op is not (Op.AtomicLoad or Op.AtomicExchange) && scalar.Kind is not (ScalarKind.Sint or ScalarKind.Uint))
                throw Error("Atomic opcode is incompatible with its scalar type.");
            if (op is Op.AtomicCompareExchange or Op.AtomicCompareExchangeWeak)
            {
                if (op == Op.AtomicCompareExchangeWeak) throw Error("OpAtomicCompareExchangeWeak requires the unsupported Kernel capability; shader modules must use OpAtomicCompareExchange.");
                return AtomicCall("spirvAtomicCompareExchange", pointer, [Value(a[7]), Value(a[6])], type,
                    AtomicMemory(a[3], a[4], a[5]));
            }
            string name = op switch
            {
                Op.AtomicLoad => "atomicLoad", Op.AtomicExchange => "atomicExchange", Op.AtomicIAdd or Op.AtomicIIncrement or Op.AtomicFAddEXT => "atomicAdd",
                Op.AtomicISub or Op.AtomicIDecrement => "atomicSub", Op.AtomicSMin or Op.AtomicUMin => "atomicMin", Op.AtomicSMax or Op.AtomicUMax => "atomicMax",
                Op.AtomicAnd => "atomicAnd", Op.AtomicOr => "atomicOr", Op.AtomicXor => "atomicXor", _ => throw Error("Unsupported atomic opcode.")
            };
            Expression one = new Expression.Literal(scalar.Kind == ScalarKind.Uint ? scalar.Width == 8 ? (object)1ul : 1u : scalar.Width == 8 ? 1L : 1, type);
            Expression[] args = op == Op.AtomicLoad ? [] : op is Op.AtomicIIncrement or Op.AtomicIDecrement ? [one] : [Value(a[5])];
            return AtomicCall(name, pointer, args, type, memory);
        }

        private Expression LowerImageAtomic(Op op, uint[] a)
        {
            Count(6, 6); var memory = AtomicMemory(a[3], a[4]);
            var texel = imageTexelPointers[a[2]];
            if (ConstantUint(texel.Sample) != 0) throw Error("Image atomic sample must be zero.");
            Expression texture = Value(texel.Image);
            if (texture.Type is not ShaderType.Image image || image.Component != Type(a[0])) throw Error("Invalid image atomic scalar type.");
            if (Type(texel.Type) is not ShaderType.Pointer { Base: ShaderType.Scalar pointee } || pointee != image.Component) throw Error("Image texel pointer scalar type mismatch.");
            if (op is Op.AtomicSMin or Op.AtomicSMax ? image.Component.Kind != ScalarKind.Sint : op is Op.AtomicUMin or Op.AtomicUMax && image.Component.Kind != ScalarKind.Uint)
                throw Error("Image atomic opcode signedness mismatch.");
            var (coordinate, layer) = Coordinates(image, Value(texel.Coordinate));
            string name = op switch
            {
                Op.AtomicIAdd => "textureAtomicAdd", Op.AtomicSMin or Op.AtomicUMin => "textureAtomicMin", Op.AtomicSMax or Op.AtomicUMax => "textureAtomicMax",
                Op.AtomicAnd => "textureAtomicAnd", Op.AtomicOr => "textureAtomicOr", Op.AtomicXor => "textureAtomicXor", _ => throw Error("Unsupported image atomic opcode.")
            };
            var arguments = new List<Expression> { texture, coordinate }; if (layer is not null) arguments.Add(layer); arguments.Add(Value(a[5]));
            return new Expression.Call(name, arguments, new ShaderType.Void()) { AtomicMemory = memory };
        }
    }
}

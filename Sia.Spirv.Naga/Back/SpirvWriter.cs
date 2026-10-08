using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Back;

public sealed record SpirvWriteOptions(bool AdjustCoordinateSpace = true, bool ClampFragmentDepth = true,
    bool ZeroInitializeWorkgroupMemory = true, bool EmitIntegerDivisionChecks = true)
{
    /// <summary>Resolve WGSL overrides before writing. Keys are explicit decimal IDs or names for overrides without IDs.</summary>
    public IReadOnlyDictionary<string, double>? PipelineConstants { get; init; }
    /// <summary>Emit unresolved workgroup sizes with LocalSizeId. Vulkan targets require maintenance4 (Vulkan 1.3), or equivalent target support.</summary>
    public bool UseLocalSizeId { get; init; }
}

public static partial class SpirvWriter
{
    public static byte[] Write(Module module, SpirvWriteOptions? options = null)
    {
        Valid.ModuleValidator.Validate(module);
        if (options?.PipelineConstants is { } values) module = Proc.PipelineConstantResolver.Resolve(module, values);
        module = Proc.QueryHelperInliner.Run(module);
        module = Proc.QueryHelperInliner.RunNonFunctionPointers(module);
        Valid.ModuleValidator.Validate(module);
        return new Writer(module, options ?? new()).Write().ToBytes();
    }
    public static uint[] WriteWords(Module module, SpirvWriteOptions? options = null)
    {
        Valid.ModuleValidator.Validate(module);
        if (options?.PipelineConstants is { } values) module = Proc.PipelineConstantResolver.Resolve(module, values);
        module = Proc.QueryHelperInliner.Run(module);
        module = Proc.QueryHelperInliner.RunNonFunctionPointers(module);
        Valid.ModuleValidator.Validate(module);
        return new Writer(module, options ?? new()).Write().ToWords();
    }

    private sealed partial class Writer(Module module, SpirvWriteOptions options)
    {
        private SpirvWriteOptions Options => options;
        private bool VulkanMemoryModel => module.VulkanMemoryModel;
        private bool UsesVulkanMemoryModel => VulkanMemoryModel || capabilities.Contains(5345);
        private uint nextId = 1;
        private readonly SortedSet<uint> capabilities = [1];
        private readonly SortedSet<string> extensions = new(StringComparer.Ordinal);
        private readonly HashSet<uint> nonUniformValues = [];
        private readonly Dictionary<uint, ShaderType> nonUniformResources = [];
        private void NonUniform(uint id, ShaderType resource)
        {
            extensions.Add("SPV_EXT_descriptor_indexing"); capabilities.Add(5301);
            capabilities.Add(resource switch { ShaderType.AccelerationStructure => 5301u, ShaderType.Pointer { Space: AddressSpace.Uniform } => 5306u, ShaderType.Pointer => 5308u, ShaderType.Image { StorageFormat: not null } => 5309u, _ => 5307u });
            Decorate(id, 5300); nonUniformValues.Add(id); nonUniformResources[id] = resource;
        }
        private readonly List<SpirvInstruction> entryPoints = [], executionModes = [], debug = [], annotations = [], declarations = [], bodies = [];
        private readonly Dictionary<ShaderType, uint> types = [];
        private readonly Dictionary<(uint Base, uint Storage), uint> pointerTypes = [];
        private readonly Dictionary<string, uint> functionTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> imageTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> decorationKeys = new(StringComparer.Ordinal);
        private readonly HashSet<ShaderType> bufferTypes = [];
        private readonly Dictionary<string, Symbol> globals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (uint Id, ShaderFunction Function)> functions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> constantIds = new(StringComparer.Ordinal);
        private readonly HashSet<uint> specializationIds = module.Constants.Where(c => c.IsOverride && c.OverrideId is not null).Select(c => c.OverrideId!.Value).ToHashSet();
        private uint nextSpecializationId;
        private uint? glsl;
        private bool usesDeviceScope;
        private bool usesSequentialMemoryOrder;
        private bool meshShaderModule;
        private readonly HashSet<uint> moduleVariableIds = [];
        private readonly Dictionary<uint, HashSet<uint>> functionGlobalUses = [], functionCalls = [];
        private sealed record Symbol(uint Id, ShaderType Type, bool Place, uint Storage = 7, bool BufferWrapper = false, ShaderType? PhysicalType = null, UniformLocation? UniformLocation = null, MemoryDecorations? MemoryRequirement = null);
        private ShaderException Error(string message, SourceSpan span = default) => new(DiagnosticStage.SpirvWrite, message, span);
        private uint Id() => nextId++;
        private static SpirvInstruction I(Op op, params uint[] args) => new((ushort)op, args);
        private void Name(uint id, string name) => debug.Add(I(Op.Name, new uint[] { id }.Concat(SpirvBinary.StringWords(name)).ToArray()));
        private void Decorate(uint id, uint decoration, params uint[] operands)
        {
            uint[] values = new uint[] { id, decoration }.Concat(operands).ToArray();
            if (decorationKeys.Add(string.Join(',', values))) annotations.Add(I(Op.Decorate, values));
        }
        private void MemberDecorate(uint id, uint member, uint decoration, params uint[] operands) => annotations.Add(I(Op.MemberDecorate, new uint[] { id, member, decoration }.Concat(operands).ToArray()));

        public SpirvBinary Write()
        {
            if (module.VulkanMemoryModel || UsesCooperativeMemoryModel(module)) { capabilities.Add(5345); extensions.Add("SPV_KHR_vulkan_memory_model"); }
            if (UsesVulkanMemoryModel && module.Globals.Any(g => g.Space == AddressSpace.TaskPayload
                && ((g.MemoryDecorations | TypeMemory(g.Type)) & MemoryDecorations.Coherent) != 0))
                throw Error("Coherent task-payload memory requires a supported non-private lowering.");
            bool PerPrimitive(ShaderType type) => type is ShaderType.Structure s && s.Members.Any(m => m.Binding?.PerPrimitive == true || PerPrimitive(m.Type));
            meshShaderModule = module.Enables.Contains("wgpu_mesh_shader") || module.Functions.Any(f => f.Stage is ShaderStage.Task or ShaderStage.Mesh)
                || module.Globals.Any(g => g.Space == AddressSpace.TaskPayload)
                || module.Structures.Any(s => s.Members.Any(m => m.Binding?.PerPrimitive == true))
                || module.Functions.Any(f => f.ReturnBinding?.PerPrimitive == true || PerPrimitive(f.ReturnType)
                    || f.Arguments.Any(a => a.Binding?.PerPrimitive == true || PerPrimitive(a.Type)));
            if (meshShaderModule) { capabilities.Add(5283); extensions.Add("SPV_EXT_mesh_shader"); }
            foreach (var global in module.Globals.Where(g => g.Space is AddressSpace.Uniform or AddressSpace.Storage or AddressSpace.Immediate)) MarkBufferType(global.Type);
            foreach (var function in module.Functions) functions.Add(function.Name, (Id(), function));
            foreach (var constant in module.Constants)
            {
                if (IsAbstract(constant.Type)) continue;
                uint value = constant.IsOverride ? Override(constant) : constant.IsSpecialization ? Specialization(constant.Value!) : Constant(constant.Value ?? throw Error("Constant has no initializer."));
                globals.Add(constant.Name, new(value, constant.Type, false)); Name(value, constant.Name);
            }
            foreach (var variable in module.Globals)
            {
                if (variable.Space == AddressSpace.Function) throw Error("Function-space module variable.");
                uint storage = Storage(variable.Space);
                bool buffer = variable.Space is AddressSpace.Uniform or AddressSpace.Storage or AddressSpace.Immediate;
                bool wrap = buffer && variable.Type is not ShaderType.BindingArray && !(variable.Type is ShaderType.Structure structure && TypeLayout.Of(structure).IsRuntimeSized);
                if (buffer && variable.Type is ShaderType.BindingArray bufferArray)
                {
                    if (bufferArray.Element is not ShaderType.Structure) throw Error("Buffer binding array elements currently require a structure.");
                }
                ShaderType physical = variable.Space == AddressSpace.Uniform ? UniformType(variable.Type) : variable.Space == AddressSpace.Workgroup ? WorkgroupType(variable.Type) : variable.Type;
                MarkBufferTypeIfNeeded(physical, buffer);
                ShaderType type = physical;
                if (wrap)
                {
                    var block = new ShaderType.Structure("SpirvBlock_" + variable.Name, [new StructMember("value", type, Offset: 0)]);
                    MarkBufferType(block);
                    uint blockId = Type(block); Decorate(blockId, 2); type = block;
                }
                uint typeId = Type(type), pointer = Pointer(typeId, storage), id = Id();
                if (buffer && !wrap) Decorate(type is ShaderType.BindingArray array ? Type(array.Element) : typeId, 2);
                if (variable.Initializer is not null)
                {
                    if (storage != 6) throw Error("Only private globals can have initializers.");
                    declarations.Add(I(Op.Variable, pointer, id, storage, Specialization(variable.Initializer)));
                }
                else if (storage == 6) declarations.Add(I(Op.Variable, pointer, id, storage, Null(type)));
                else declarations.Add(I(Op.Variable, pointer, id, storage));
                Name(id, variable.Name); globals.Add(variable.Name, new(id, variable.Type, true, storage, wrap, physical)); moduleVariableIds.Add(id);
                if (variable.Binding is { } binding) { Decorate(id, 34, binding.Group); Decorate(id, 33, binding.Binding); }
                if ((variable.MemoryDecorations & MemoryDecorations.Coherent) != 0 && !UsesVulkanMemoryModel) Decorate(id, 23);
                if ((variable.MemoryDecorations & MemoryDecorations.Volatile) != 0 && !UsesVulkanMemoryModel) Decorate(id, 21);
                if (variable.Space == AddressSpace.Storage)
                {
                    if ((variable.Access & StorageAccess.Write) == 0) Decorate(id, 24);
                    if ((variable.Access & StorageAccess.Read) == 0) Decorate(id, 25);
                }
                ShaderType resource = variable.Type is ShaderType.BindingArray bindingArray ? bindingArray.Element : variable.Type;
                if (resource is ShaderType.Image { StorageFormat: not null } storageImage)
                {
                    if ((storageImage.Access & StorageAccess.Read) == 0) Decorate(id, 25);
                    if ((storageImage.Access & StorageAccess.Write) == 0) Decorate(id, 24);
                }
            }
            foreach (var function in module.Functions) EmitFunction(functions[function.Name].Id, function);
            foreach (var function in module.Functions.Where(f => f.Stage is not null)) EmitEntryPoint(function);
            if (capabilities.Contains(5345) && usesSequentialMemoryOrder)
                throw Error("Sequentially consistent memory order is unavailable with the required Vulkan memory model.");
            if (capabilities.Contains(5345) && usesDeviceScope) capabilities.Add(5346);
            var all = new List<SpirvInstruction>();
            all.AddRange(capabilities.Select(c => I(Op.Capability, c)));
            all.AddRange(extensions.Select(e => I(Op.Extension, SpirvBinary.StringWords(e))));
            if (glsl is uint import) all.Add(I(Op.ExtInstImport, new uint[] { import }.Concat(SpirvBinary.StringWords("GLSL.std.450")).ToArray()));
            all.Add(I(Op.MemoryModel, 0, capabilities.Contains(5345) ? 3u : 1u)); all.AddRange(entryPoints); all.AddRange(executionModes);
            all.AddRange(debug); all.AddRange(annotations); all.AddRange(declarations); all.AddRange(bodies);
            return new() { Version = meshShaderModule ? 0x00010400u : 0x00010300u, Bound = nextId, Instructions = all };
        }

        private static bool IsAbstract(ShaderType type) => type switch
        {
            ShaderType.Scalar { Kind: ScalarKind.AbstractInt or ScalarKind.AbstractFloat } => true,
            ShaderType.Vector v => IsAbstract(v.Component), ShaderType.Matrix m => IsAbstract(m.Component),
            ShaderType.Array a => IsAbstract(a.Element), ShaderType.Structure s => s.Members.Any(m => IsAbstract(m.Type)), _ => false
        };
        private uint Storage(AddressSpace space) => space switch
        {
            AddressSpace.Handle => 0, AddressSpace.Uniform => 2, AddressSpace.Workgroup => 4, AddressSpace.Private => 6,
            AddressSpace.Function => 7, AddressSpace.Immediate => 9, AddressSpace.Storage => 12, AddressSpace.TaskPayload => 5402, _ => throw Error("Unknown address space.")
        };
        private uint Pointer(uint baseType, uint storage)
        {
            if (pointerTypes.TryGetValue((baseType, storage), out uint id)) return id;
            id = Id(); declarations.Add(I(Op.TypePointer, id, storage, baseType)); pointerTypes.Add((baseType, storage), id); return id;
        }
        private uint Type(ShaderType type)
        {
            if (type is ShaderType.Sampler { Comparison: true }) return Type(new ShaderType.Sampler());
            if (type is ShaderType.RayQuery { VertexReturn: true }) return Type(new ShaderType.RayQuery());
            if (type is ShaderType.AccelerationStructure { VertexReturn: true }) return Type(new ShaderType.AccelerationStructure());
            if (type is ShaderType.Pointer pointer) return Pointer(Type(pointer.Space == AddressSpace.Workgroup ? WorkgroupType(pointer.Base) : pointer.Base), Storage(pointer.Space));
            if (type is ShaderType.Atomic atomic)
            {
                if (atomic.Component.Kind == ScalarKind.Float)
                {
                    capabilities.Add(6033); // AtomicFloat32AddEXT
                    extensions.Add("SPV_EXT_shader_atomic_float_add");
                }
                if (atomic.Component.Width == 8) capabilities.Add(12);
                return Type(atomic.Component);
            }
            if (types.TryGetValue(type, out uint id)) return id;
            id = Id();
            switch (type)
            {
                case ShaderType.Void: declarations.Add(I(Op.TypeVoid, id)); break;
                case ShaderType.Scalar { Kind: ScalarKind.Bool }: declarations.Add(I(Op.TypeBool, id)); break;
                case ShaderType.Scalar s when s.Kind is ScalarKind.Sint or ScalarKind.Uint:
                    if (s.Width == 8) capabilities.Add(11);
                    if (s.Width == 2) { capabilities.Add(22); capabilities.Add(4433); capabilities.Add(4434); }
                    if (s.Width is not (2 or 4 or 8)) throw Error("Unsupported integer width.");
                    declarations.Add(I(Op.TypeInt, id, (uint)s.Width * 8, s.Kind == ScalarKind.Sint ? 1u : 0u)); break;
                case ShaderType.Scalar { Kind: ScalarKind.Float } s:
                    if (s.Width == 2) { capabilities.Add(9); capabilities.Add(4433); capabilities.Add(4434); }
                    if (s.Width == 8) capabilities.Add(10);
                    if (s.Width is not (2 or 4 or 8)) throw Error("Unsupported floating-point width.");
                    declarations.Add(I(Op.TypeFloat, id, (uint)s.Width * 8)); break;
                case ShaderType.Vector vector:
                    declarations.Add(I(Op.TypeVector, id, Type(vector.Component), (uint)vector.Size)); break;
                case ShaderType.Matrix matrix:
                    capabilities.Add(0);
                    declarations.Add(I(Op.TypeMatrix, id, Type(new ShaderType.Vector(matrix.Rows, matrix.Component)), (uint)matrix.Columns)); break;
                case ShaderType.CooperativeMatrix matrix:
                    capabilities.Add(6022); capabilities.Add(5345);
                    extensions.Add("SPV_KHR_cooperative_matrix"); extensions.Add("SPV_KHR_vulkan_memory_model");
                    declarations.Add(I(Op.TypeCooperativeMatrixKHR, id, Type(matrix.Component), Constant(Expression.U32(matrix.Scope)),
                        Constant(Expression.U32((uint)matrix.Rows)), Constant(Expression.U32((uint)matrix.Columns)), Constant(Expression.U32((uint)matrix.Role)))); break;
                case ShaderType.Array array:
                    if (array.OverrideLength is string length) declarations.Add(I(Op.TypeArray, id, Type(array.Element), SpecializationLength(length)));
                    else if (array.Length is uint count) declarations.Add(I(Op.TypeArray, id, Type(array.Element), Constant(Expression.U32(count))));
                    else declarations.Add(I(Op.TypeRuntimeArray, id, Type(array.Element)));
                    if (bufferTypes.Contains(type)) Decorate(id, 6, array.Stride ?? TypeLayout.Of(array.Element).Stride); break;
                case ShaderType.BindingArray array:
                    if (array.Element is not (ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure or ShaderType.Structure)) throw Error("Unsupported binding array element.");
                    if (array.OverrideLength is string bindingLength) declarations.Add(I(Op.TypeArray, id, Type(array.Element), SpecializationLength(bindingLength)));
                    else if (array.Length is uint bindingCount) declarations.Add(I(Op.TypeArray, id, Type(array.Element), Constant(Expression.U32(bindingCount))));
                    else
                    {
                        extensions.Add("SPV_EXT_descriptor_indexing"); capabilities.Add(5302);
                        declarations.Add(I(Op.TypeRuntimeArray, id, Type(array.Element)));
                    }
                    break;
                case ShaderType.Structure structure:
                    uint[] members = structure.Members.Select(m => Type(m.Type)).ToArray();
                    declarations.Add(I(Op.TypeStruct, new uint[] { id }.Concat(members).ToArray())); Name(id, structure.Name);
                    if (!UsesVulkanMemoryModel)
                        for (int index = 0; index < structure.Members.Count; index++)
                        {
                            var memory = structure.Members[index].MemoryDecorations;
                            if ((memory & MemoryDecorations.Coherent) != 0) MemberDecorate(id, (uint)index, 23);
                            if ((memory & MemoryDecorations.Volatile) != 0) MemberDecorate(id, (uint)index, 21);
                        }
                    if (!bufferTypes.Contains(type)) break;
                    uint offset = 0;
                    for (int index = 0; index < structure.Members.Count; index++)
                    {
                        var member = structure.Members[index]; var layout = TypeLayout.Of(member.Type);
                        offset = member.Offset ?? TypeLayout.RoundUp(member.Alignment ?? layout.Alignment, offset);
                        MemberDecorate(id, (uint)index, 35, offset);
                        ShaderType element = member.Type;
                        while (element is ShaderType.Array arr) element = arr.Element;
                        if (element is ShaderType.Matrix m)
                        {
                            MemberDecorate(id, (uint)index, 5);
                            MemberDecorate(id, (uint)index, 7, TypeLayout.Of(new ShaderType.Vector(m.Rows, m.Component)).Stride);
                        }
                        offset = checked(offset + (member.Size ?? layout.Size));
                    }
                    break;
                case ShaderType.Sampler: declarations.Add(I(Op.TypeSampler, id)); break;
                case ShaderType.RayQuery:
                    capabilities.Add(4472); extensions.Add("SPV_KHR_ray_query");
                    declarations.Add(I(Op.TypeRayQueryKHR, id)); break;
                case ShaderType.AccelerationStructure:
                    capabilities.Add(4472); extensions.Add("SPV_KHR_ray_query");
                    declarations.Add(I(Op.TypeAccelerationStructureKHR, id)); break;
                case ShaderType.Image image:
                    if (image.StorageFormat == "r64uint") { capabilities.Add(5016); extensions.Add("SPV_EXT_shader_image_int64"); }
                    else if (image.StorageFormat is string format && StorageImageFormats.Extended(ImageFormat(format))) capabilities.Add(49);
                    if (image.Dimension == ImageDimension.D1) capabilities.Add(image.StorageFormat is null ? 43u : 44u);
                    if (image.Dimension == ImageDimension.Cube && image.Arrayed) capabilities.Add(45);
                    uint[] imageOperands = [Type(image.Component), (uint)image.Dimension, image.Depth ? 1u : 0u,
                        image.Arrayed ? 1u : 0u, image.Multisampled ? 1u : 0u, image.StorageFormat is null ? 1u : 2u,
                        image.StorageFormat is null ? 0u : ImageFormat(image.StorageFormat)];
                    string imageKey = string.Join(',', imageOperands);
                    if (imageTypes.TryGetValue(imageKey, out uint imageId)) { types.Add(type, imageId); return imageId; }
                    declarations.Add(I(Op.TypeImage, new uint[] { id }.Concat(imageOperands).ToArray())); imageTypes.Add(imageKey, id);
                    break;
                default: throw Error($"Unsupported SPIR-V type {type}.");
            }
            types.Add(type, id); return id;
        }

        private void MarkBufferType(ShaderType type)
        {
            if (!bufferTypes.Add(type)) return;
            if (type is ShaderType.Array array) MarkBufferType(array.Element);
            if (type is ShaderType.BindingArray bindingArray) MarkBufferType(bindingArray.Element);
            if (type is ShaderType.Structure structure) foreach (var member in structure.Members) MarkBufferType(member.Type);
        }

        private uint Null(ShaderType type)
        {
            uint typeId = Type(type); string key = "null:" + typeId;
            if (constantIds.TryGetValue(key, out uint id)) return id;
            id = Id(); declarations.Add(I(Op.ConstantNull, typeId, id)); constantIds.Add(key, id); return id;
        }
        private uint Constant(Expression expression)
        {
            if (!ConstantEvaluator.TryEvaluate(expression, out var value)) throw Error("Expected a constant value.", expression.Span);
            uint typeId = Type(value.Type);
            uint[] data; Op op;
            if (value is Expression.Literal literal)
            {
                if (literal.Value is bool b) { op = b ? Op.ConstantTrue : Op.ConstantFalse; data = []; }
                else
                {
                    op = Op.Constant;
                    data = literal.Value switch
                    {
                        ushort u => [u], short i => [unchecked((uint)i)], uint u => [u], int i => [unchecked((uint)i)], float f => [BitConverter.SingleToUInt32Bits(f)], Half h => [BitConverter.HalfToUInt16Bits(h)],
                        ulong u => [(uint)u, (uint)(u >> 32)], long l => [unchecked((uint)l), unchecked((uint)(l >> 32))],
                        double d => [(uint)BitConverter.DoubleToUInt64Bits(d), (uint)(BitConverter.DoubleToUInt64Bits(d) >> 32)], _ => throw Error("Unsupported constant literal.")
                    };
                }
            }
            else if (value is Expression.Construct construct)
            {
                if (construct.Components.Count == 0) return Null(construct.Type);
                op = Op.ConstantComposite; data = construct.Components.Select(Constant).ToArray();
            }
            else throw Error("Expected a literal or composite constant.");
            string key = $"{(uint)op}:{typeId}:" + string.Join(',', data);
            if (constantIds.TryGetValue(key, out uint existing)) return existing;
            uint id = Id(); declarations.Add(I(op, new uint[] { typeId, id }.Concat(data).ToArray())); constantIds.Add(key, id); return id;
        }
        private uint Override(ShaderConstant constant)
        {
            Expression value = constant.Value ?? throw Error($"Override '{constant.Name}' has no default; supply PipelineConstants to resolve its value.");
            if (!ConstantEvaluator.TryEvaluate(value, out var evaluated) || evaluated is not Expression.Literal literal) throw Error("Dependent override defaults require PipelineConstants resolution before SPIR-V writing.");
            uint ordinary = Constant(literal), id = Id();
            var source = declarations.First(i => i.Operands.Length > 1 && i.Operands[1] == ordinary && (Op)i.Opcode is Op.Constant or Op.ConstantTrue or Op.ConstantFalse);
            Op op = (Op)source.Opcode switch { Op.ConstantTrue => Op.SpecConstantTrue, Op.ConstantFalse => Op.SpecConstantFalse, _ => Op.SpecConstant };
            declarations.Add(I(op, new uint[] { Type(constant.Type), id }.Concat(source.Operands[2..]).ToArray()));
            uint overrideId;
            if (constant.OverrideId is uint explicitId) overrideId = explicitId;
            else
            {
                while (specializationIds.Contains(nextSpecializationId)) nextSpecializationId++;
                if (nextSpecializationId > ushort.MaxValue) throw Error("No available 16-bit specialization constant ID.");
                overrideId = nextSpecializationId++; specializationIds.Add(overrideId);
            }
            Decorate(id, 1, overrideId); return id;
        }
        private uint FunctionType(ShaderType result, IEnumerable<ShaderType> arguments)
        {
            uint[] signature = new uint[] { Type(result) }.Concat(arguments.Select(Type)).ToArray();
            string key = string.Join(',', signature);
            if (functionTypes.TryGetValue(key, out uint known)) return known;
            uint id = Id(); declarations.Add(I(Op.TypeFunction, new uint[] { id }.Concat(signature).ToArray())); functionTypes.Add(key, id); return id;
        }
        private uint GlslImport() => glsl ??= Id();
        private uint ImageFormat(string format) => StorageImageFormats.TryCode(format, out uint code) ? code : throw Error($"Unsupported storage image format '{format}'.");
    }
}

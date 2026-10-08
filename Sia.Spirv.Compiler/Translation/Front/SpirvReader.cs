using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    public static Module Parse(ReadOnlySpan<byte> bytes, SpirvReadOptions? options = null) => ReadBinary(SpirvBinary.Parse(bytes), options);
    public static Module Parse(ReadOnlySpan<uint> words, SpirvReadOptions? options = null) => ReadBinary(SpirvBinary.Parse(words), options);
    private static Module ReadBinary(SpirvBinary binary, SpirvReadOptions? options)
    {
        var normalized = PointerPhiLowering.RunWithHelpers(binary, out var helpers, out var snapshots);
        return new Reader(normalized, options ?? new(), helpers, snapshots).Read();
    }

    private sealed partial class Reader(SpirvBinary binary, SpirvReadOptions options, IReadOnlySet<uint> specializedHelpers, IReadOnlyDictionary<uint, string> descriptorSnapshots)
    {
        private readonly Module module = new() { WorkgroupInitializationRequired = false };
        private readonly Dictionary<uint, ShaderType> types = [];
        private readonly Dictionary<uint, Expression> values = [];
        private readonly Dictionary<uint, string> names = [];
        private readonly Dictionary<(uint, uint), string> memberNames = [];
        private readonly Dictionary<(uint Id, int Member, uint Decoration), uint[]> decorations = [];
        private readonly Dictionary<uint, ShaderFunction> functions = [];
        private readonly Dictionary<uint, (GlobalVariable Variable, uint Storage, IoBinding? Binding)> globals = [];
        private readonly List<Entry> entries = [];
        private readonly Dictionary<uint, uint[]> workgroups = [];
        private readonly Dictionary<uint, uint[]> workgroupIds = [];
        private readonly HashSet<uint> earlyDepth = [];
        private readonly Dictionary<uint, string> conservativeDepth = [];
        private readonly HashSet<uint> defined = [];
        private readonly HashSet<string> accessedInterfaceBuiltins = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, string> imports = [];
        private bool variablePointers;
        private bool fullVariablePointers;
        private bool pointerSelection;
        private int pointerCaptureCounter;
        private readonly Dictionary<string, string> descriptorSnapshotNames = new(StringComparer.Ordinal);
        private SpirvInstruction current = new(0, []);
        private sealed record Entry(uint Id, string Name, ShaderStage Stage, uint[] Interfaces);

        private ShaderException Error(string message) => new(DiagnosticStage.SpirvParse, message,
            new(current.WordOffset * 4, current.WordCount * 4));
        private void Count(int minimum, int? maximum = null)
        {
            if (current.Operands.Length < minimum || maximum is int max && current.Operands.Length > max)
                throw Error($"Invalid operand count for {(Op)current.Opcode}.");
        }
        private void Define(uint id)
        {
            if (id == 0 || id >= binary.Bound) throw Error($"ID %{id} exceeds header bound {binary.Bound}.");
            if (!defined.Add(id)) throw Error($"ID %{id} is defined twice.");
        }
        private ShaderType Type(uint id) => types.TryGetValue(id, out var type) ? type : throw Error($"Undefined type %{id}.");
        private Expression Value(uint id) => imageAtomicResults.Contains(id) ? throw Error("WGSL texture atomics cannot return an old value; this SPIR-V result is used.") : values.TryGetValue(id, out var value) ? value : throw Error($"Undefined value %{id}.");
        private uint? Decoration(uint id, uint decoration, int member = -1) =>
            decorations.TryGetValue((id, member, decoration), out var operands) && operands.Length != 0 ? operands[0] : null;
        private bool HasDecoration(uint id, uint decoration, int member = -1) => decorations.ContainsKey((id, member, decoration));
        private string Name(uint id, string prefix = "v") => names.TryGetValue(id, out string? name)
            ? "n_" + Identifier(name) + "_" + id : prefix == "f" ? "naga_fn" + id : prefix + id;
        private static string Identifier(string name)
        {
            string result = new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            return result.Length == 0 ? "unnamed" : char.IsAsciiDigit(result[0]) || WgslKeywords.IsReserved(result) ? "n_" + result : result;
        }

        public Module Read()
        {
            // Names/decorations precede types in logical SPIR-V layout.
            foreach (var instruction in binary.Instructions)
            {
                current = instruction;
                var a = instruction.Operands;
                switch ((Op)instruction.Opcode)
                {
                    case Op.Name: Count(2); names[a[0]] = SpirvBinary.ReadString(a.AsSpan(1), out _); break;
                    case Op.MemberName: Count(3); memberNames[(a[0], a[1])] = SpirvBinary.ReadString(a.AsSpan(2), out _); break;
                    case Op.Decorate: Count(2); decorations[(a[0], -1, a[1])] = a[2..]; break;
                    case Op.MemberDecorate: Count(3); decorations[(a[0], checked((int)a[1]), a[2])] = a[3..]; break;
                    case Op.EntryPoint:
                        Count(3);
                        string name = SpirvBinary.ReadString(a.AsSpan(2), out int length);
                        ShaderStage stage = a[0] switch { 0 => ShaderStage.Vertex, 4 => ShaderStage.Fragment, 5 => ShaderStage.Compute, 5364 => ShaderStage.Task, 5365 => ShaderStage.Mesh, _ => throw Error($"Unsupported execution model {a[0]}.") };
                        entries.Add(new(a[1], Identifier(name), stage, a[(2 + length)..]));
                        break;
                    case Op.ExecutionMode:
                        Count(2);
                        if (a[1] == 17) { Count(5, 5); workgroups[a[0]] = a[2..]; }
                        else if (a[1] is 26 or 5270) { Count(3, 3); meshModes[(a[0], a[1])] = a[2]; }
                        else if (a[1] is 27 or 5269 or 5298) { Count(2, 2); meshModes[(a[0], 0)] = a[1]; }
                        else if (a[1] == 9) earlyDepth.Add(a[0]);
                        else if (a[1] is 14 or 15 or 16) conservativeDepth[a[0]] = a[1] switch { 14 => "greater_equal", 15 => "less_equal", _ => "unchanged" };
                        else if (a[1] is not (7 or 12 or 14 or 15 or 16)) throw Error($"Unsupported execution mode {a[1]}.");
                        break;
                    case Op.ExecutionModeId:
                        Count(5, 5);
                        if (a[1] != 38) throw Error($"Unsupported ID execution mode {a[1]}.");
                        workgroupIds[a[0]] = a[2..];
                        break;
                }
            }

            bool memoryModel = false;
            for (int index = 0; index < binary.Instructions.Count; index++)
            {
                current = binary.Instructions[index];
                var a = current.Operands;
                switch ((Op)current.Opcode)
                {
                    case Op.Nop: case Op.Source: case Op.SourceContinued: case Op.SourceExtension:
                    case Op.Name: case Op.MemberName: case Op.Decorate: case Op.MemberDecorate:
                    case Op.EntryPoint: case Op.ExecutionMode: case Op.ExecutionModeId: case Op.Line: case Op.NoLine: case Op.ModuleProcessed:
                        break;
                    case Op.String: Count(2); Define(a[0]); break;
                    case Op.Extension:
                        string extension = SpirvBinary.ReadString(a, out _);
                        if (extension == "SPV_KHR_variable_pointers") break;
                        if (extension is not ("SPV_KHR_storage_buffer_storage_class" or "SPV_KHR_vulkan_memory_model" or "SPV_KHR_16bit_storage" or "SPV_EXT_descriptor_indexing" or "SPV_KHR_non_semantic_info" or "SPV_KHR_multiview" or "SPV_KHR_shader_draw_parameters" or "SPV_KHR_fragment_shader_barycentric" or "SPV_NV_fragment_shader_barycentric" or "SPV_EXT_shader_atomic_float_add" or "SPV_EXT_shader_image_int64" or "SPV_KHR_ray_query" or "SPV_KHR_ray_tracing_position_fetch" or "SPV_KHR_cooperative_matrix" or "SPV_EXT_mesh_shader"))
                            throw Error($"Unsupported SPIR-V extension '{extension}'.");
                        break;
                    case Op.Capability:
                        Count(1, 1);
                        if (a[0] is 4441 or 4442) { variablePointers = true; fullVariablePointers |= a[0] == 4442; break; }
                        if (a[0] is >= 62 and <= 68) break;
                        if (a[0] == 12) break; // Int64Atomics; the scalar and atomic instructions are checked separately.
                        if (a[0] == 6033) break; // AtomicFloat32AddEXT
                        if (a[0] == 5016) break; // Int64ImageEXT
                        if (a[0] == 55) break; // StorageImageReadWithoutFormat
                        if (a[0] is 4472 or 5391) break; // RayQueryKHR / RayQueryPositionFetchKHR
                        if (a[0] is 6022 or 5346) break; // CooperativeMatrixKHR / VulkanMemoryModelDeviceScope
                        if (a[0] == 5283) { module.Enables.Add("wgpu_mesh_shader"); break; }
                        if (a[0] is 4427 or 4439 or 5284) break;
                        if (a[0] is not (0 or 1 or 2 or 9 or 10 or 11 or 22 or 23 or 25 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 39 or 43 or 44 or 45 or 49 or 50 or 51 or 52 or 56 or 61 or 4433 or 4434 or 4435 or 4436 or 5301 or 5302 or 5306 or 5307 or 5308 or 5309 or 5345))
                            throw Error($"Unsupported capability {a[0]}.");
                        break;
                    case Op.MemoryModel:
                        Count(2, 2);
                        if (memoryModel || a[0] != 0 || a[1] is not (0 or 1 or 3)) throw Error("Expected one logical SPIR-V memory model.");
                        module.VulkanMemoryModel = a[1] == 3; memoryModel = true; break;
                    case Op.ExtInstImport: Count(2); Define(a[0]); imports[a[0]] = SpirvBinary.ReadString(a.AsSpan(1), out _); break;
                    case Op.ExtInst:
                        if (!SkipDebugInstruction()) throw Error("Semantic extended instruction is outside a function.");
                        break;
                    case Op.TypeVoid: Count(1, 1); AddType(a[0], new ShaderType.Void()); break;
                    case Op.TypeBool: Count(1, 1); AddType(a[0], ShaderType.Bool); break;
                    case Op.TypeInt:
                        Count(3, 3);
                        if (a[1] is not (16 or 32 or 64) || a[2] > 1) throw Error("Unsupported integer width or signedness.");
                        AddType(a[0], new ShaderType.Scalar(a[2] == 0 ? ScalarKind.Uint : ScalarKind.Sint, (int)a[1] / 8));
                        if (a[1] == 16) module.Enables.Add("wgpu_int16");
                        break;
                    case Op.TypeFloat:
                        Count(2, 2);
                        if (a[1] is not (16 or 32 or 64)) throw Error("Unsupported floating-point width.");
                        AddType(a[0], new ShaderType.Scalar(ScalarKind.Float, (int)a[1] / 8));
                        if (a[1] == 16) module.Enables.Add("f16");
                        break;
                    case Op.TypeVector:
                        Count(3, 3);
                        if (Type(a[1]) is not ShaderType.Scalar scalar || a[2] is < 2 or > 4) throw Error("Invalid vector type.");
                        AddType(a[0], new ShaderType.Vector((int)a[2], scalar)); break;
                    case Op.TypeMatrix:
                        Count(3, 3);
                        if (Type(a[1]) is not ShaderType.Vector vector || vector.Component.Kind != ScalarKind.Float || a[2] is < 2 or > 4) throw Error("Invalid matrix type.");
                        AddType(a[0], new ShaderType.Matrix((int)a[2], vector.Size, vector.Component)); break;
                    case Op.TypeArray:
                        Count(3, 3);
                        ReadArrayType(a[0], a[1], a[2]);
                        break;
                    case Op.TypeCooperativeMatrixKHR:
                        Count(6, 6);
                        if (Type(a[1]) is not ShaderType.Scalar coopScalar || coopScalar is not { Kind: ScalarKind.Float, Width: 2 or 4 }) throw Error("Unsupported cooperative matrix component.");
                        uint coopRows = ConstantUint(a[3]), coopColumns = ConstantUint(a[4]), coopRole = ConstantUint(a[5]);
                        if (coopRows is not (8 or 16) || coopColumns is not (8 or 16) || coopRole > 2) throw Error("Unsupported cooperative matrix dimensions or role.");
                        AddType(a[0], new ShaderType.CooperativeMatrix((int)coopColumns, (int)coopRows, coopScalar, (CooperativeRole)coopRole, ConstantUint(a[2])));
                        module.Enables.Add("wgpu_cooperative_matrix"); break;
                    case Op.TypeRuntimeArray:
                        Count(2, 2); AddResourceArrayType(a[0], a[1], null); break;
                    case Op.TypeStruct:
                        Count(1);
                        var members = new List<StructMember>();
                        for (int m = 1; m < a.Length; m++)
                        {
                            ShaderType element = Type(a[m]);
                            while (element is ShaderType.Array array) element = array.Element;
                            bool hasRowMajor = HasDecoration(a[0], 4, m - 1), hasColMajor = HasDecoration(a[0], 5, m - 1);
                            if (hasRowMajor && hasColMajor || element is not ShaderType.Matrix && (hasRowMajor || hasColMajor || HasDecoration(a[0], 7, m - 1)))
                                throw Error("Invalid native matrix layout decorations.");
                            string member = memberNames.TryGetValue((a[0], (uint)(m - 1)), out string? raw) ? "n_" + Identifier(raw) + "_" + (m - 1) : "m" + (m - 1);
                            members.Add(new(member, Type(a[m]), Decoration(a[0], 35, m - 1), Binding: Io(a[0], m - 1))
                            {
                                MemoryDecorations = (HasDecoration(a[0], 23, m - 1) ? MemoryDecorations.Coherent : MemoryDecorations.None)
                                    | (HasDecoration(a[0], 21, m - 1) ? MemoryDecorations.Volatile : MemoryDecorations.None)
                            });
                            if (element is ShaderType.Matrix matrix)
                            {
                                bool rowMajor = hasRowMajor;
                                uint natural = TypeLayout.Of(new ShaderType.Vector(matrix.Rows, matrix.Component)).Stride;
                                uint stride = Decoration(a[0], 7, m - 1) ?? (rowMajor
                                    ? TypeLayout.Of(new ShaderType.Vector(matrix.Columns, matrix.Component)).Stride : natural);
                                if (rowMajor || stride != natural) matrixLayouts.Add(members[^1], new(stride, rowMajor));
                            }
                        }
                        // Boolean-containing structures cannot be host-shareable.
                        // Their logical SPIR-V member offsets do not describe a
                        // buffer ABI (Naga's RayIntersection has a packed vec2).
                        // Normalize that irrelevant metadata to a natural layout.
                        if (members.Any(m => ContainsBoolean(m.Type))) members = members.Select(m =>
                        {
                            var normalized = m with { Offset = null };
                            if (matrixLayouts.Remove(m, out var layout)) matrixLayouts.Add(normalized, layout);
                            return normalized;
                        }).ToList();
                        var structure = new ShaderType.Structure(Name(a[0], "S"), members);
                        AddType(a[0], structure); module.Structures.Add(structure); break;
                    case Op.TypePointer:
                        Count(3, 3); AddType(a[0], new ShaderType.Pointer(Type(a[2]), a[1] == 11 ? AddressSpace.Handle : Space(a[1])));
                        if (a[1] == 11) imagePointerTypes.Add(a[0]);
                        break;
                    case Op.TypeFunction: Count(2); Define(a[0]); functionTypes[a[0]] = a[1..]; break;
                    case Op.TypeSampler: Count(1, 1); AddType(a[0], new ShaderType.Sampler()); break;
                    case Op.TypeRayQueryKHR: case Op.TypeAccelerationStructureKHR:
                        Count(1, 1);
                        bool vertexReturn = binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands is [5391]);
                        AddType(a[0], (Op)current.Opcode == Op.TypeRayQueryKHR ? new ShaderType.RayQuery(vertexReturn) : new ShaderType.AccelerationStructure(vertexReturn));
                        module.Enables.Add("wgpu_ray_query");
                        if (vertexReturn) module.Enables.Add("wgpu_ray_query_vertex_return");
                        break;
                    case Op.TypeImage:
                        Count(8, 9);
                        if (Type(a[1]) is not ShaderType.Scalar component || a[2] > 3 || a[4] > 1 || a[5] > 1 || a[6] > 2) throw Error("Invalid image type.");
                        string? format = a[6] == 2 ? ImageFormat(a[7]) : null;
                        if (format is not null && component != StorageImageFormats.Component(a[7])) throw Error("Storage image component does not match its format.");
                        AddType(a[0], new ShaderType.Image((ImageDimension)a[2], component, a[4] != 0, a[5] != 0, a[3] == 1, format,
                            StorageAccess.ReadWrite)); break;
                    case Op.TypeSampledImage: Count(2, 2); AddType(a[0], Type(a[1])); break;
                    case Op.ConstantTrue: case Op.ConstantFalse: case Op.SpecConstantTrue: case Op.SpecConstantFalse:
                        Count(2, 2); AddConstant(a[1], new Expression.Literal((Op)current.Opcode is Op.ConstantTrue or Op.SpecConstantTrue, Type(a[0])), (Op)current.Opcode is Op.SpecConstantTrue or Op.SpecConstantFalse); break;
                    case Op.Constant: case Op.SpecConstant:
                        Count(3); AddConstant(a[1], DecodeLiteral(Type(a[0]), a.AsSpan(2)), (Op)current.Opcode == Op.SpecConstant); break;
                    case Op.ConstantComposite: case Op.SpecConstantComposite:
                        Count(2); AddConstant(a[1], new Expression.Construct(Type(a[0]), a[2..].Select(Value).ToArray()), false); break;
                    case Op.SpecConstantOp:
                        Count(4); Define(a[1]); values[a[1]] = CaptureSpecialization(a[1], SpecializationOperation()); break;
                    case Op.ConstantNull:
                        Count(2, 2); AddConstant(a[1], new Expression.Construct(Type(a[0]), []), false);
                        pointerSelection |= Type(a[0]) is ShaderType.Pointer; break;
                    case Op.Undef: Count(2, 2); Define(a[1]); values[a[1]] = new Expression.Construct(Type(a[0]), []); break;
                    case Op.Variable: ReadGlobal(); break;
                    case Op.Function: ReadFunction(ref index); break;
                    default: throw Error($"Unsupported module instruction {(Op)current.Opcode} ({current.Opcode}).");
                }
            }
            if (!memoryModel) throw Error("Missing OpMemoryModel.");
            if (module.VulkanMemoryModel && binary.Instructions.FirstOrDefault(i =>
                (Op)i.Opcode == Op.Decorate && i.Operands[1] is 21 or 23
                || (Op)i.Opcode == Op.MemberDecorate && i.Operands[2] is 21 or 23) is { } banned)
            {
                current = banned;
                throw Error("Coherent/volatile decorations are banned with the Vulkan memory model; use per-access operands.");
            }
            UpgradeAtomicGlobals();
            UpgradeComparisonResources();
            PrepareMeshEntries();
            ResolveFunctions();
            AddEntryPoints();
            Module result = matrixLayouts.Count != 0 || variablePointers ? Proc.QueryHelperInliner.RunNonFunctionPointers(module,
                specializedHelpers.Select(id => functions[id].Name).ToHashSet(StringComparer.Ordinal)) : module;
            if (pointerSelection) result = new PointerSelectionLowering(this, result, fullVariablePointers).Run();
            return matrixLayouts.Count == 0 ? result : new MatrixLayoutLowering(result, matrixLayouts).Run();
        }

        private void AddType(uint id, ShaderType type) { Define(id); types.Add(id, type); }
        private bool SkipDebugInstruction()
        {
            if ((Op)current.Opcode != Op.ExtInst || current.Operands.Length < 3 || !imports.TryGetValue(current.Operands[2], out string? import) || import != "NonSemantic.Shader.DebugInfo.100") return false;
            Count(4); _ = Type(current.Operands[0]); Define(current.Operands[1]);
            return true;
        }
        private void AddResourceArrayType(uint id, uint elementId, uint? length, string? overrideLength = null)
        {
            ShaderType element = Type(elementId);
            bool bufferDescriptors = element is ShaderType.Structure && (HasDecoration(elementId, 2) || HasDecoration(elementId, 3))
                && binary.Instructions.Any(i => (Op)i.Opcode == Op.TypePointer && i.Operands is [_, 2 or 12, var pointee] && pointee == id);
            if (element is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure || bufferDescriptors)
            {
                AddType(id, new ShaderType.BindingArray(element, length) { OverrideLength = overrideLength }); module.Enables.Add("wgpu_binding_array");
            }
            else AddType(id, new ShaderType.Array(element, length, Decoration(id, 6)) { OverrideLength = overrideLength });
        }
        private static bool ContainsBoolean(ShaderType type) => type switch
        {
            ShaderType.Scalar { Kind: ScalarKind.Bool } or ShaderType.Vector { Component.Kind: ScalarKind.Bool } => true,
            ShaderType.Structure s => s.Members.Any(m => ContainsBoolean(m.Type)), ShaderType.Array a => ContainsBoolean(a.Element), _ => false
        };
        private void AddConstant(uint id, Expression expression, bool isOverride)
        {
            Define(id);
            if (expression is Expression.Construct { Type: ShaderType.Scalar, Components.Count: 0 })
                expression = ConstantEvaluator.Evaluate(expression);
            if (isOverride && Decoration(id, 1) is not null)
            {
                var constant = new ShaderConstant(Name(id, "c"), expression.Type, expression, true, Decoration(id, 1));
                module.Constants.Add(constant); values[id] = new Expression.Reference(constant.Name, constant.Type);
            }
            else values[id] = expression;
        }
        private uint ConstantUint(uint id) => ConstantEvaluator.TryEvaluate(Value(id), out var value) && value is Expression.Literal l ? l.Value switch
        {
            uint u => u, int i when i >= 0 => (uint)i, _ => throw Error("Expected nonnegative 32-bit integer constant.")
        } : throw Error("Expected integer constant.");

        private Expression DecodeLiteral(ShaderType type, ReadOnlySpan<uint> words)
        {
            if (type is not ShaderType.Scalar scalar) throw Error("OpConstant must be a scalar.");
            int count = scalar.Width > 4 ? 2 : 1;
            if (words.Length != count) throw Error("Literal word count does not match its scalar width.");
            object value = (scalar.Kind, scalar.Width) switch
            {
                (ScalarKind.Uint, 2) => (object)unchecked((ushort)words[0]), (ScalarKind.Sint, 2) => unchecked((short)words[0]),
                (ScalarKind.Uint, 4) => words[0], (ScalarKind.Sint, 4) => unchecked((int)words[0]),
                (ScalarKind.Float, 4) => BitConverter.UInt32BitsToSingle(words[0]),
                (ScalarKind.Float, 2) => BitConverter.UInt16BitsToHalf((ushort)words[0]),
                (ScalarKind.Uint, 8) => ((ulong)words[1] << 32) | words[0],
                (ScalarKind.Sint, 8) => unchecked((long)(((ulong)words[1] << 32) | words[0])),
                (ScalarKind.Float, 8) => BitConverter.UInt64BitsToDouble(((ulong)words[1] << 32) | words[0]),
                _ => throw Error("Unsupported scalar constant.")
            };
            return new Expression.Literal(value, type);
        }

        private AddressSpace Space(uint storage) => storage switch
        {
            0 => AddressSpace.Handle, 1 or 3 or 6 => AddressSpace.Private,
            2 => AddressSpace.Uniform, 4 => AddressSpace.Workgroup, 7 => AddressSpace.Function,
            9 => AddressSpace.Immediate, 12 => AddressSpace.Storage, 5402 => AddressSpace.TaskPayload,
            _ => throw Error($"Unsupported storage class {storage}.")
        };

        private void ReadGlobal()
        {
            Count(3, 4); var a = current.Operands;
            if (Type(a[0]) is not ShaderType.Pointer pointer) throw Error("OpVariable requires a pointer type.");
            if (a[2] == 7) throw Error("Function variable outside a function.");
            Define(a[1]);
            AddressSpace space = Space(a[2]);
            // Vulkan 1.0 storage buffers use Uniform + BufferBlock.
            uint pointeeId = binary.Instructions.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands[0] == a[0]).Operands[2];
            if (a[2] == 2 && HasDecoration(pointeeId, 3)) space = AddressSpace.Storage;
            StorageAccess access = space == AddressSpace.Uniform ? StorageAccess.Read : StorageAccess.ReadWrite;
            if (HasDecoration(a[1], 24) || AllMembersDecorated(pointeeId, 24)) access &= ~StorageAccess.Write;
            if (HasDecoration(a[1], 25) || AllMembersDecorated(pointeeId, 25)) access &= ~StorageAccess.Read;
            ResourceBinding? binding = Decoration(a[1], 33) is uint slot ? new(Decoration(a[1], 34) ?? 0, slot) : null;
            ShaderType dataType = pointer.Base;
            dataType = ResourceAccess(dataType, access);
            var variable = new GlobalVariable(Name(a[1], "g"), dataType, space, access, binding, a.Length == 4 ? Value(a[3]) : null)
            {
                MemoryDecorations = (HasDecoration(a[1], 23) ? MemoryDecorations.Coherent : MemoryDecorations.None)
                    | (HasDecoration(a[1], 21) ? MemoryDecorations.Volatile : MemoryDecorations.None)
            };
            module.Globals.Add(variable); globals.Add(a[1], (variable, a[2], Io(a[1])));
            values[a[1]] = new Expression.Reference(variable.Name, space == AddressSpace.Handle ? variable.Type : new ShaderType.Pointer(variable.Type, space, access));
        }

        private static ShaderType ResourceAccess(ShaderType type, StorageAccess access) => type switch
        {
            ShaderType.Image { StorageFormat: not null } image => image with { Access = access },
            ShaderType.BindingArray array => array with { Element = ResourceAccess(array.Element, access) }, _ => type
        };

        private bool AllMembersDecorated(uint id, uint decoration) => Type(id) is ShaderType.Structure structure
            && structure.Members.Count > 0 && Enumerable.Range(0, structure.Members.Count).All(m => HasDecoration(id, decoration, m));

        private IoBinding? Io(uint id, int member = -1)
        {
            uint? location = Decoration(id, 30, member), builtin = Decoration(id, 11, member);
            if (location is null && builtin is null) return null;
            string? interpolation = HasDecoration(id, 5285, member) ? "per_vertex" : HasDecoration(id, 14, member) ? "flat" : HasDecoration(id, 13, member) ? "linear" : null;
            string? sampling = HasDecoration(id, 16, member) ? "centroid" : HasDecoration(id, 17, member) ? "sample" : null;
            if (sampling is not null && interpolation is null) interpolation = "perspective";
            // Builtin interpolation is intrinsic; WGSL only accepts this attribute on locations.
            if (builtin is not null) { interpolation = null; sampling = null; }
            return new(location, builtin is uint b ? Builtin(b) : null, interpolation, sampling, HasDecoration(id, 18, member), Decoration(id, 32, member), location is not null && HasDecoration(id, 5271, member));
        }

        private string Builtin(uint value) => value switch
        {
            0 => "position", 1 => "point_size", 3 => "clip_distances", 4 => "cull_distance",
            5 or 42 => "vertex_index", 6 or 43 => "instance_index", 7 => "primitive_index",
            4426 => "draw_index", 4440 => "view_index", 5286 => "barycentric", 5287 => "barycentric_no_perspective",
            15 => "position", 17 => "front_facing", 18 => "sample_index", 20 => "sample_mask",
            22 => "frag_depth", 24 => "num_workgroups", 26 => "workgroup_id", 27 => "local_invocation_id",
            28 => "global_invocation_id", 29 => "local_invocation_index", 36 => "subgroup_size",
            38 => "num_subgroups", 40 => "subgroup_id", 41 => "subgroup_invocation_id",
            5294 => "point_index", 5295 => "line_indices", 5296 => "triangle_indices", 5299 => "cull_primitive", _ => throw Error($"Unsupported builtin {value}.")
        };

        private string ImageFormat(uint format) => StorageImageFormats.Name(format) ?? throw Error(format == 0
            ? "Formatless storage image has no WGSL format; supply an image with a typed SPIR-V format."
            : $"Unsupported storage image format {format}.");

        private void AddEntryPoints()
        {
            foreach (var entry in entries)
            {
                if (!functions.TryGetValue(entry.Id, out var callee)) throw Error($"Undefined entry function %{entry.Id}.");
                if (callee.Arguments.Count != 0 || callee.ReturnType is not ShaderType.Void) throw Error("SPIR-V entry functions must be void with no parameters.");
                var function = new ShaderFunction(entry.Name) { Stage = entry.Stage, EarlyDepthTest = earlyDepth.Contains(entry.Id), ConservativeDepth = conservativeDepth.GetValueOrDefault(entry.Id) };
                uint[] sizeBuiltins = decorations.Where(d => d.Key.Member == -1 && d.Key.Decoration == 11 && d.Value is [25] && values.ContainsKey(d.Key.Id)).Select(d => d.Key.Id).ToArray();
                if (entry.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh && sizeBuiltins.Length != 0)
                {
                    if (sizeBuiltins.Length != 1 || Value(sizeBuiltins[0]).Type is not ShaderType.Vector { Size: 3, Component.Kind: ScalarKind.Sint or ScalarKind.Uint, Component.Width: 4 }) throw Error("WorkgroupSize must be one three-component 32-bit integer constant.");
                    function.WorkgroupSize = Enumerable.Range(0, 3).Select(i => SpecIndex(Value(sizeBuiltins[0]), (uint)i)).ToArray();
                }
                else if (workgroupIds.TryGetValue(entry.Id, out var ids)) function.WorkgroupSize = ids.Select(Value).ToArray();
                else if (workgroups.TryGetValue(entry.Id, out var size)) function.WorkgroupSize = size.Select(Expression.U32).Cast<Expression>().ToArray();
                else if (entry.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh) throw Error("Workgroup entry point is missing LocalSize.");
                ConfigureMeshEntry(entry, function);
                var outputs = new List<(Expression Place, StructMember Member)>();
                foreach (uint id in entry.Interfaces)
                {
                    if (!globals.TryGetValue(id, out var global)) throw Error($"Invalid entry interface %{id}.");
                    if (entry.Stage == ShaderStage.Mesh && global.Storage == 3) continue;
                    if (global.Storage is not (1 or 3)) continue;
                    if (global.Variable.Type is ShaderType.Structure structure)
                    {
                        foreach (var member in structure.Members)
                        {
                            // glslang declares these even when no function accesses them.
                            if (global.Storage == 3 && member.Binding?.Builtin is "point_size" or "clip_distances" or "cull_distance" && !accessedInterfaceBuiltins.Contains(member.Binding.Builtin)) continue;
                            Interface(new Expression.Member(Value(id), member.Name, new ShaderType.Pointer(member.Type, AddressSpace.Private)), member.Type, member.Binding, global.Variable.Name + "_" + member.Name);
                        }
                    }
                    else Interface(Value(id), global.Variable.Type, global.Binding, global.Variable.Name);

                    void Interface(Expression place, ShaderType type, IoBinding? binding, string name)
                    {
                        if (binding is null) throw Error("Entry IO requires a location or builtin.");
                        if (binding.Location.HasValue && HasDecoration(id, 5271)) binding = binding with { PerPrimitive = true };
                        if (binding.Builtin == "sample_mask" && type is ShaderType.Array { Length: 1 } mask)
                        { place = Index(place, Expression.U32(0)); type = mask.Element; }
                        if (global.Storage == 1)
                        {
                            ShaderType interfaceType = type;
                            if (binding.Builtin is "vertex_index" or "instance_index" or "primitive_index" or "sample_index" or "sample_mask" or "local_invocation_index" or "draw_index" or "view_index" or "subgroup_size" or "num_subgroups" or "subgroup_id" or "subgroup_invocation_id")
                                interfaceType = ShaderType.U32;
                            else if (binding.Builtin is "global_invocation_id" or "local_invocation_id" or "workgroup_id" or "num_workgroups") interfaceType = new ShaderType.Vector(3, ShaderType.U32);
                            if (interfaceType != type && !(interfaceType == ShaderType.U32 && type == ShaderType.I32 || interfaceType == new ShaderType.Vector(3, ShaderType.U32) && type == new ShaderType.Vector(3, ShaderType.I32)))
                                throw Error("Builtin input has an incompatible type.");
                            function.Arguments.Add(new(name + "_input", interfaceType, binding));
                            Expression value = new Expression.Reference(name + "_input", interfaceType);
                            if (interfaceType != type) value = new Expression.Convert(type, value, true);
                            function.Body.Statements.Add(new Statement.Store(place, value));
                        }
                        else outputs.Add((place, new(name + "_output", type, Binding: binding)));
                    }
                }
                InitializeMeshEntryControl(entry, function);
                function.Body.Statements.Add(new Statement.Evaluate(new Expression.Call(callee.Name, [], new ShaderType.Void())));
                if (entry.Stage == ShaderStage.Task) function.Body.Statements.Add(new Statement.Return(new Expression.Load(taskDispatchSize!)));
                AdjustMeshPosition(entry, function);
                if (options.AdjustCoordinateSpace)
                    foreach (var output in outputs.Where(o => o.Member.Binding?.Builtin == "position"))
                    {
                        Expression y = Index(output.Place, Expression.U32(1));
                        function.Body.Statements.Add(new Statement.Store(y, new Expression.Unary("-", new Expression.Load(y), ValueTypeOf(y.Type))));
                    }
                if (outputs.Count != 0)
                {
                    var result = new ShaderType.Structure("Output_" + entry.Id, outputs.Select(o => o.Member).ToArray());
                    module.Structures.Add(result); function.ReturnType = result;
                    function.Body.Statements.Add(new Statement.Return(new Expression.Construct(result, outputs.Select(o => new Expression.Load(o.Place)).Cast<Expression>().ToArray())));
                }
                module.Functions.Add(function);
            }
        }

        private static ShaderType ValueTypeOf(ShaderType type) => type is ShaderType.Pointer pointer ? pointer.Base : type;
    }
}

public sealed record SpirvReadOptions(bool AdjustCoordinateSpace = true);

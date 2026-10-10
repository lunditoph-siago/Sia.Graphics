using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed record SpirvIoDecoration(uint Decoration, IReadOnlyList<uint> Operands);
internal sealed record SpirvInterfaceMetadata(uint Storage, IReadOnlyList<SpirvIoDecoration> Decorations,
    IReadOnlySet<uint> Capabilities, IReadOnlySet<string> Extensions);
internal sealed record SpirvEntryExecutionMode(uint Mode, IReadOnlyList<uint> Literals,
    IReadOnlyList<SpirvSpecializationValue>? Values = null);
internal sealed record SpirvEntryPointMetadata(string Name, uint Stage, string? TaskPayload, bool IncludeGlobalInterfaces,
    IReadOnlyList<SpirvEntryExecutionMode> Modes, SpirvSpecializationValue? WorkgroupBuiltin,
    IReadOnlyDictionary<EntryInterfaceField, SpirvInterfaceMetadata> Interfaces);
internal sealed record SpirvOverrideConstant(ShaderType Type, Expression.Literal Default, uint SpecId);
internal sealed record SpirvEntryAbi(uint Version, bool MeshShaderModule,
    IReadOnlySet<uint> Capabilities, IReadOnlySet<string> Extensions,
    IReadOnlyDictionary<string, SpirvEntryPointMetadata> Entries,
    IReadOnlyDictionary<MeshOutputField, SpirvInterfaceMetadata> MeshInterfaces,
    IReadOnlyDictionary<Expression, SpirvSpecializationValue> Specializations,
    IReadOnlyDictionary<string, SpirvSpecializationValue> ArrayLengths,
    IReadOnlyDictionary<string, SpirvSpecializationValue> ArrayLengthsU32,
    IReadOnlyDictionary<string, SpirvOverrideConstant> Overrides);

// Entry policy and specialization identity belong to target preparation. The
// serializer only resolves these immutable values to numeric SPIR-V IDs.
internal static class SpirvEntryMetadataLowering
{
    internal static SpirvPhysicalLayout Prepare(SpirvPhysicalLayout layout, bool useLocalSizeId = false, uint? version = null)
    {
        var module = layout.Module;
        bool PerPrimitive(ShaderType type) => type is ShaderType.Structure s && s.Members.Any(m => m.Binding?.PerPrimitive == true || PerPrimitive(m.Type));
        bool mesh = module.Enables.Contains("wgpu_mesh_shader") || module.Functions.Any(f => f.Stage is ShaderStage.Task or ShaderStage.Mesh)
            || module.Globals.Any(g => g.Space == AddressSpace.TaskPayload) || module.Structures.Any(PerPrimitive)
            || module.Functions.Any(f => f.ReturnBinding?.PerPrimitive == true || PerPrimitive(f.ReturnType)
                || f.Arguments.Any(a => a.Binding?.PerPrimitive == true || PerPrimitive(a.Type)));
        uint selectedVersion = version ?? (mesh ? 0x00010400u : 0x00010300u);
        if (mesh && selectedVersion < 0x00010400) throw Error("Mesh/task shaders require SPIR-V 1.4 or later.");
        var overrides = new Dictionary<string, SpirvOverrideConstant>(StringComparer.Ordinal);
        var reservedIds = module.Constants.Where(c => c.IsOverride && c.OverrideId is not null).Select(c => c.OverrideId!.Value).ToHashSet();
        uint nextSpecId = 0;
        foreach (var constant in module.Constants.Where(c => c.IsOverride && !Abstract(c.Type))) {
            var value = constant.Value ?? throw Error($"Override '{constant.Name}' has no default; supply PipelineConstants to resolve its value.");
            if (!ConstantEvaluator.TryEvaluate(value, out var evaluated) || evaluated is not Expression.Literal literal)
                throw Error("Dependent override defaults require PipelineConstants resolution before SPIR-V writing.");
            uint id;
            if (constant.OverrideId is uint explicitId) id = explicitId;
            else {
                while (reservedIds.Contains(nextSpecId)) nextSpecId++;
                if (nextSpecId > ushort.MaxValue) throw Error("No available 16-bit specialization constant ID.");
                id = nextSpecId++; reservedIds.Add(id);
            }
            overrides.Add(constant.Name, new(constant.Type, literal, id));
        }
        var specialization = new SpirvSpecializationLowering(module);
        foreach (var constant in module.Constants.Where(c => c.IsSpecialization && !c.IsOverride))
            _ = specialization.Lower(constant.Value ?? throw Error("Constant has no initializer."));
        foreach (var global in module.Globals.Where(g => g.Initializer is not null)) _ = specialization.Lower(global.Initializer!);
        var defaults = PipelineConstantResolver.DefaultExpressions(module);
        uint DefaultSize(Expression expression)
        {
            if (defaults(expression) is not Expression.Literal literal) throw Error("Workgroup size default must be an integer.");
            try {
                uint size = System.Convert.ToUInt32(literal.Value);
                if (size == 0) throw Error("Workgroup size default must be positive; supply PipelineConstants to replace it.");
                return size;
            } catch (OverflowException) { throw Error("Workgroup size default must be positive and fit in u32; supply PipelineConstants to replace it."); }
        }
        var compute = module.Functions.Where(f => f.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh).ToArray();
        var sizes = compute.ToDictionary(f => f.Name, f => f.WorkgroupSize.Select(e => specialization.Convert(ShaderType.U32, e)).ToArray(), StringComparer.Ordinal);
        var unresolved = compute.Where(f => !f.WorkgroupSize.All(e => ConstantEvaluator.TryEvaluateRuntime(e, out _)))
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        SpirvSpecializationValue? shared = null;
        if (!useLocalSizeId && unresolved.Count != 0) {
            var first = sizes[compute[0].Name];
            if (sizes.Values.Any(s => !s.Select(v => v.Identity).SequenceEqual(first.Select(v => v.Identity))))
                throw Error("Different compute workgroup sizes require UseLocalSizeId or PipelineConstants resolution.");
            shared = specialization.Composite(new ShaderType.Vector(3, ShaderType.U32), first);
        }
        var entries = new Dictionary<string, SpirvEntryPointMetadata>(StringComparer.Ordinal);
        foreach (var entry in module.Functions.Where(f => f.Stage is not null)) {
            if (!layout.EntryWrappers.TryGetValue(entry.Name, out var wrapper)) continue;
            var modes = new List<SpirvEntryExecutionMode>();
            void Mode(uint mode, params uint[] literals) => modes.Add(new(mode, Array.AsReadOnly(literals)));
            if (wrapper.Publication is { } publication && entry.Stage == ShaderStage.Mesh) {
                Mode(publication.Topology); Mode(26, publication.MaxVertices); Mode(5270, publication.MaxPrimitives);
            }
            if (entry.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh) {
                uint[] literalSizes = entry.WorkgroupSize.Select(DefaultSize).ToArray();
                if (useLocalSizeId && unresolved.Contains(entry.Name)) modes.Add(new(38, Array.Empty<uint>(), Array.AsReadOnly(sizes[entry.Name])));
                else Mode(17, literalSizes);
            }
            if (entry.Stage == ShaderStage.Fragment) {
                Mode(7); if (wrapper.WritesDepth) Mode(12); if (entry.EarlyDepthTest) Mode(9);
                if (entry.ConservativeDepth is string depth) Mode(depth switch {
                    "greater_equal" => 14u, "less_equal" => 15u, "unchanged" => 16u,
                    _ => throw Error("Invalid conservative depth mode.")
                });
            }
            uint stage = entry.Stage switch {
                ShaderStage.Vertex => 0, ShaderStage.Fragment => 4, ShaderStage.Compute => 5, ShaderStage.Task => 5364, ShaderStage.Mesh => 5365,
                _ => throw Error("Missing entry point stage.")
            };
            entries.Add(entry.Name, new(entry.Name, stage, entry.TaskPayload, selectedVersion >= 0x00010400, modes.AsReadOnly(),
                unresolved.Contains(entry.Name) ? shared : null,
                wrapper.Interfaces.ToFrozenDictionary(f => f, f => Interface(f.Type, f.Binding, f.Input, mesh))));
        }
        var meshInterfaces = new Dictionary<MeshOutputField, SpirvInterfaceMetadata>();
        void Body(Block body)
        {
            foreach (var statement in body.Statements) switch (statement) {
                case Statement.MeshStore store:
                    meshInterfaces.TryAdd(store.Field, Interface(store.Field.Type, store.Field.Binding, false, mesh, store.Field.PerPrimitive)); break;
                case Statement.Nested nested: Body(nested.Body); break;
                case Statement.If branch: Body(branch.Accept); Body(branch.Reject); break;
                case Statement.Loop loop: Body(loop.Body); Body(loop.Continuing); break;
                case Statement.Switch choice: foreach (var arm in choice.Cases) Body(arm.Body); break;
            }
        }
        foreach (var function in module.Functions) Body(function.Body);
        foreach (var function in layout.ControlFlow.Values)
            foreach (var store in function.Graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.MeshStore>())
                meshInterfaces.TryAdd(store.Field, Interface(store.Field.Type, store.Field.Binding, false, mesh, store.Field.PerPrimitive));
        var lengths = new Dictionary<string, SpirvSpecializationValue>(StringComparer.Ordinal);
        var lengthsU32 = new Dictionary<string, SpirvSpecializationValue>(StringComparer.Ordinal);
        var defaultValues = PipelineConstantResolver.DefaultValues(module);
        var visited = new HashSet<ShaderType>();
        void Type(ShaderType type)
        {
            if (!visited.Add(type)) return;
            string? length = type switch { ShaderType.Array a => a.OverrideLength, ShaderType.BindingArray a => a.OverrideLength, _ => null };
            if (length is not null && !lengths.ContainsKey(length)) {
                var constant = module.Constants.FirstOrDefault(c => c.Name == length) ?? throw Error("Array length specialization must precede its type.");
                if (defaultValues(length) is not Expression.Literal value) throw Error("Array length specialization default must be an integer.");
                try { if (System.Convert.ToUInt32(value.Value) == 0) throw Error("Array length default must be positive; supply PipelineConstants to replace it."); }
                catch (OverflowException) { throw Error("Array length default must be positive and fit in u32; supply PipelineConstants to replace it."); }
                var reference = new Expression.Reference(length, constant.Type);
                lengths.Add(length, specialization.Lower(reference)); lengthsU32.Add(length, specialization.Convert(ShaderType.U32, reference));
            }
            switch (type) {
                case ShaderType.Array array: Type(array.Element); break;
                case ShaderType.BindingArray array: Type(array.Element); break;
                case ShaderType.Structure structure: foreach (var member in structure.Members) Type(member.Type); break;
                case ShaderType.Pointer pointer: Type(pointer.Base); break;
            }
        }
        void BodyTypes(Block body)
        {
            foreach (var statement in body.Statements) switch (statement) {
                case Statement.Declare declare: Type(declare.Type); break;
                case Statement.Evaluate evaluate: Type(evaluate.Value.Type); break;
                case Statement.Return { Value: { } value }: Type(value.Type); break;
                case Statement.Store store: Type(store.Target.Type); Type(store.Value.Type); break;
                case Statement.Nested nested: BodyTypes(nested.Body); break;
                case Statement.If branch: BodyTypes(branch.Accept); BodyTypes(branch.Reject); break;
                case Statement.Loop loop: BodyTypes(loop.Body); BodyTypes(loop.Continuing); break;
                case Statement.Switch choice: foreach (var arm in choice.Cases) BodyTypes(arm.Body); break;
            }
        }
        foreach (var type in module.Structures.Cast<ShaderType>().Concat(module.Constants.Select(c => c.Type))
            .Concat(layout.Globals.Values.Select(g => g.DeclarationType))) Type(type);
        foreach (var function in module.Functions) { Type(function.ReturnType); foreach (var arg in function.Arguments) Type(arg.Type); BodyTypes(function.Body); }
        return layout with { EntryAbi = new(selectedVersion, mesh, (mesh ? new uint[] { 5283 } : []).ToFrozenSet(),
            (mesh ? new[] { "SPV_EXT_mesh_shader" } : []).ToFrozenSet(StringComparer.Ordinal), entries.ToFrozenDictionary(StringComparer.Ordinal),
            meshInterfaces.ToFrozenDictionary(), specialization.Expressions.ToFrozenDictionary(ReferenceEqualityComparer.Instance),
            lengths.ToFrozenDictionary(StringComparer.Ordinal), lengthsU32.ToFrozenDictionary(StringComparer.Ordinal),
            overrides.ToFrozenDictionary(StringComparer.Ordinal)) };
    }
    private static SpirvInterfaceMetadata Interface(ShaderType type, IoBinding binding, bool input, bool mesh, bool perPrimitive = false)
    {
        var decorations = new List<SpirvIoDecoration>(); var capabilities = new HashSet<uint>(); var extensions = new HashSet<string>(StringComparer.Ordinal);
        void Decorate(uint decoration, params uint[] operands) => decorations.Add(new(decoration, Array.AsReadOnly(operands)));
        ShaderType ioType = binding.Interpolation == "per_vertex" && type is ShaderType.Array a ? a.Element : type;
        if (ioType is ShaderType.Scalar { Width: 2 } or ShaderType.Vector { Component.Width: 2 }) capabilities.Add(4436);
        if (input && binding.Builtin == "sample_mask") Decorate(14);
        if (binding.Location is uint location) Decorate(30, location);
        if (binding.BlendSource is uint source) Decorate(32, source);
        if (binding.PerPrimitive) { Decorate(5271); capabilities.Add(5283); extensions.Add("SPV_EXT_mesh_shader"); }
        if (binding.Builtin is string builtin) {
            if (builtin == "primitive_index" && !mesh) capabilities.Add(2);
            if (builtin == "sample_index") capabilities.Add(35);
            if (builtin == "clip_distances") capabilities.Add(32);
            if (builtin == "cull_distance") capabilities.Add(33);
            if (builtin == "view_index") { capabilities.Add(4439); extensions.Add("SPV_KHR_multiview"); }
            if (builtin == "draw_index") { capabilities.Add(4427); extensions.Add("SPV_KHR_shader_draw_parameters"); }
            if (builtin is "barycentric" or "barycentric_no_perspective") { capabilities.Add(5284); extensions.Add("SPV_KHR_fragment_shader_barycentric"); }
            if (builtin is "subgroup_id" or "subgroup_size" or "subgroup_invocation_id" or "num_subgroups") capabilities.Add(61);
            if (builtin is "primitive_index" or "sample_index" or "front_facing" or "view_index" && input) Decorate(14);
            Decorate(11, builtin switch {
                "position" => input ? 15u : 0u, "point_size" => 1, "clip_distances" => 3, "cull_distance" => 4,
                "vertex_index" => 42, "instance_index" => 43, "primitive_index" => 7, "front_facing" => 17,
                "draw_index" => 4426, "view_index" => 4440, "barycentric" => 5286, "barycentric_no_perspective" => 5287,
                "sample_index" => 18, "sample_mask" => 20, "frag_depth" => 22, "num_workgroups" => 24,
                "workgroup_id" => 26, "local_invocation_id" => 27, "global_invocation_id" => 28, "local_invocation_index" => 29,
                "subgroup_size" => 36, "num_subgroups" => 38, "subgroup_id" => 40, "subgroup_invocation_id" => 41,
                "point_index" => 5294, "line_indices" => 5295, "triangle_indices" => 5296, "cull_primitive" => 5299,
                _ => throw Error($"Unsupported builtin '{builtin}'.")
            });
        }
        if (binding.Invariant) Decorate(18);
        if (binding.Interpolation == "flat") Decorate(14);
        if (binding.Interpolation == "linear") Decorate(13);
        if (binding.Interpolation == "per_vertex") { Decorate(5285); capabilities.Add(5284); extensions.Add("SPV_KHR_fragment_shader_barycentric"); }
        if (binding.Sampling == "centroid") Decorate(16);
        if (binding.Sampling == "sample") { Decorate(17); capabilities.Add(35); }
        if (perPrimitive) Decorate(5271);
        return new(input ? 1u : 3u, decorations.AsReadOnly(), capabilities.ToFrozenSet(), extensions.ToFrozenSet(StringComparer.Ordinal));
    }
    private static ShaderException Error(string message) => new(DiagnosticStage.SpirvWrite, message);
    private static bool Abstract(ShaderType type) => type switch {
        ShaderType.Scalar { Kind: ScalarKind.AbstractInt or ScalarKind.AbstractFloat } => true,
        ShaderType.Vector v => Abstract(v.Component), ShaderType.Matrix m => Abstract(m.Component),
        ShaderType.Array a => Abstract(a.Element), ShaderType.Structure s => s.Members.Any(m => Abstract(m.Type)), _ => false
    };
}

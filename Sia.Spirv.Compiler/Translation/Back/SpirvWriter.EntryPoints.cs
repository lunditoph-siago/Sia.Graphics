using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Func<Expression, Expression> workgroupDefaults = Proc.PipelineConstantResolver.DefaultExpressions(module);
        private uint? specializedWorkgroupSize;

        private void EmitWorkgroupSize(ShaderFunction function, uint entry)
        {
            if (function.WorkgroupSize.All(e => ConstantEvaluator.TryEvaluateRuntime(e, out _)))
            {
                executionModes.Add(I(Op.ExecutionMode, new[] { entry, 17u }.Concat(function.WorkgroupSize.Select(DefaultWorkgroupSize)).ToArray()));
                return;
            }
            uint[] sizes = function.WorkgroupSize.Select(e => SpecializationConvert(ShaderType.U32, e)).ToArray();
            if (options.UseLocalSizeId)
            {
                foreach (var size in function.WorkgroupSize) _ = DefaultWorkgroupSize(size);
                executionModes.Add(I(Op.ExecutionModeId, new[] { entry, 38u }.Concat(sizes).ToArray()));
                return;
            }
            foreach (var other in module.Functions.Where(f => f.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh))
                if (!other.WorkgroupSize.Select(e => SpecializationConvert(ShaderType.U32, e)).SequenceEqual(sizes))
                    throw Error("Different compute workgroup sizes require UseLocalSizeId or PipelineConstants resolution.");
            if (specializedWorkgroupSize is null)
            {
                specializedWorkgroupSize = SpecializationComposite(new ShaderType.Vector(3, ShaderType.U32), sizes);
                Decorate(specializedWorkgroupSize.Value, 11, 25); // BuiltIn WorkgroupSize
            }
            executionModes.Add(I(Op.ExecutionMode, new[] { entry, 17u }.Concat(function.WorkgroupSize.Select(DefaultWorkgroupSize)).ToArray()));
        }

        private uint DefaultWorkgroupSize(Expression expression)
        {
            if (workgroupDefaults(expression) is not Expression.Literal value) throw Error("Workgroup size default must be an integer.");
            try
            {
                uint size = System.Convert.ToUInt32(value.Value);
                if (size == 0) throw Error("Workgroup size default must be positive; supply PipelineConstants to replace it.");
                return size;
            }
            catch (OverflowException) { throw Error("Workgroup size default must be positive and fit in u32; supply PipelineConstants to replace it."); }
        }

        private void EmitEntryPoint(ShaderFunction function)
        {
            uint id = Id(), signature = FunctionType(new ShaderType.Void(), []);
            var code = new List<SpirvInstruction>(); var variables = new List<SpirvInstruction>(); var interfaces = new List<uint>();
            var sampleMasks = new HashSet<uint>();
            uint R(Op op, ShaderType type, params uint[] operands)
            { uint result = Id(); code.Add(I(op, new uint[] { Type(type), result }.Concat(operands).ToArray())); return result; }
            uint Interface(ShaderType type, IoBinding binding, bool input, string name)
            {
                ShaderType ioValue = binding.Interpolation == "per_vertex" && type is ShaderType.Array a ? a.Element : type;
                if (ioValue is ShaderType.Scalar { Width: 2 } or ShaderType.Vector { Component.Width: 2 }) capabilities.Add(4436);
                if (binding.Builtin == "sample_mask") type = new ShaderType.Array(type, 1);
                uint variable = Id(); declarations.Add(I(Op.Variable, Pointer(Type(type), input ? 1u : 3u), variable, input ? 1u : 3u));
                if (binding.Builtin == "sample_mask") { sampleMasks.Add(variable); if (input) Decorate(variable, 14); }
                interfaces.Add(variable); Name(variable, name); IoDecorations(variable, binding, input); return variable;
            }
            var outputs = new List<(uint Id, ShaderType Type, IoBinding Binding, uint? Member)>();
            if (function.ReturnType is not ShaderType.Void && function.Stage != ShaderStage.Task)
            {
                if (function.ReturnType is ShaderType.Structure structure)
                    for (int member = 0; member < structure.Members.Count; member++)
                    {
                        var field = structure.Members[member]; var binding = field.Binding ?? throw Error("Entry output member has no binding.");
                        outputs.Add((Interface(field.Type, binding, false, field.Name), field.Type, binding, (uint)member));
                    }
                else
                {
                    var binding = function.ReturnBinding ?? throw Error("Entry output has no binding.");
                    outputs.Add((Interface(function.ReturnType, binding, false, function.Name + "_output"), function.ReturnType, binding, null));
                }
            }
            var inputs = new List<(FunctionArgument Argument, List<(uint Id, ShaderType Type)> Parts)>();
            uint? localInvocationIndex = null;
            foreach (var argument in function.Arguments)
            {
                var parts = new List<(uint, ShaderType)>();
                if (argument.Type is ShaderType.Structure structure)
                {
                    foreach (var field in structure.Members)
                    {
                        var binding = field.Binding ?? throw Error("Entry input member has no binding.");
                        uint variable = Interface(field.Type, binding, true, field.Name); parts.Add((variable, field.Type));
                        if (binding.Builtin == "local_invocation_index") localInvocationIndex = variable;
                    }
                }
                else
                {
                    var binding = argument.Binding ?? throw Error("Entry input has no binding.");
                    uint variable = Interface(argument.Type, binding, true, argument.Name); parts.Add((variable, argument.Type));
                    if (binding.Builtin == "local_invocation_index") localInvocationIndex = variable;
                }
                inputs.Add((argument, parts));
            }
            if (function.Stage is ShaderStage.Task or ShaderStage.Mesh)
                localInvocationIndex ??= Interface(ShaderType.U32, new(Builtin: "local_invocation_index"), true, "sia_mesh_local_invocation_index");
            if (options.ZeroInitializeWorkgroupMemory && module.WorkgroupInitializationRequired && function.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh && module.Globals.Any(g => g.Space == AddressSpace.Workgroup))
            {
                localInvocationIndex ??= Interface(ShaderType.U32, new(Builtin: "local_invocation_index"), true, "sia_local_invocation_index");
                uint index = R(Op.Load, ShaderType.U32, localInvocationIndex.Value);
                uint first = R(Op.IEqual, ShaderType.Bool, index, Constant(Expression.U32(0)));
                uint initialize = Id(), ready = Id();
                code.Add(I(Op.SelectionMerge, ready, 0)); code.Add(I(Op.BranchConditional, first, initialize, ready)); code.Add(I(Op.Label, initialize));
                bool HasAtomic(ShaderType type) => type switch { ShaderType.Atomic => true, ShaderType.Array a => HasAtomic(a.Element), ShaderType.Structure s => s.Members.Any(m => HasAtomic(m.Type)), _ => false };
                void Initialize(uint pointer, ShaderType type, MemoryDecorations inherited)
                {
                    if (!HasAtomic(type) && TypeMemory(type) == MemoryDecorations.None && type is not ShaderType.Array { OverrideLength: not null })
                    {
                        code.Add(I(Op.Store, new[] { pointer, Null(type) }.Concat(MemoryOperands(DecoratedMemory(null, inherited, AddressSpace.Workgroup, false))).ToArray())); return;
                    }
                    if (type is ShaderType.Atomic atomic)
                    {
                        uint semantics = UsesVulkanMemoryModel && (inherited & MemoryDecorations.Volatile) != 0 ? 32768u : 0u;
                        code.Add(I(Op.AtomicStore, pointer, Constant(Expression.U32(2)), Constant(Expression.U32(semantics)), Null(atomic.Component))); return;
                    }
                    if (type is ShaderType.Array { Length: uint length } array)
                        for (uint i = 0; i < length; i++) Initialize(R(Op.AccessChain, new ShaderType.Pointer(array.Element, AddressSpace.Workgroup), pointer, Constant(Expression.U32(i))), array.Element, inherited);
                    else if (type is ShaderType.Array { OverrideLength: string pending } specialized)
                    {
                        uint counter = Id(), zero = Constant(Expression.U32(0)), one = Constant(Expression.U32(1));
                        variables.Add(I(Op.Variable, Pointer(Type(ShaderType.U32), 7), counter, 7));
                        code.Add(I(Op.Store, counter, zero));
                        uint header = Id(), body = Id(), continuing = Id(), done = Id();
                        code.Add(I(Op.Branch, header)); code.Add(I(Op.Label, header));
                        uint currentIndex = R(Op.Load, ShaderType.U32, counter);
                        uint more = R(Op.ULessThan, ShaderType.Bool, currentIndex, SpecializationLengthU32(pending));
                        code.Add(I(Op.LoopMerge, done, continuing, 0)); code.Add(I(Op.BranchConditional, more, body, done)); code.Add(I(Op.Label, body));
                        Initialize(R(Op.AccessChain, new ShaderType.Pointer(specialized.Element, AddressSpace.Workgroup), pointer, currentIndex), specialized.Element, inherited);
                        code.Add(I(Op.Branch, continuing)); code.Add(I(Op.Label, continuing));
                        code.Add(I(Op.Store, counter, R(Op.IAdd, ShaderType.U32, currentIndex, one)));
                        code.Add(I(Op.Branch, header)); code.Add(I(Op.Label, done));
                    }
                    else if (type is ShaderType.Structure structure)
                        for (int i = 0; i < structure.Members.Count; i++) Initialize(R(Op.AccessChain, new ShaderType.Pointer(structure.Members[i].Type, AddressSpace.Workgroup), pointer, Constant(Expression.U32((uint)i))), structure.Members[i].Type, inherited | structure.Members[i].MemoryDecorations);
                    else throw Error("Unsupported atomic workgroup initialization.");
                }
                foreach (var global in module.Globals.Where(g => g.Space == AddressSpace.Workgroup))
                {
                    Initialize(globals[global.Name].Id, globals[global.Name].PhysicalType ?? global.Type, global.MemoryDecorations);
                    if (meshShaderModule) interfaces.Add(globals[global.Name].Id);
                }
                code.Add(I(Op.Branch, ready)); code.Add(I(Op.Label, ready));
                code.Add(I(Op.ControlBarrier, Constant(Expression.U32(2)), Constant(Expression.U32(2)), Constant(Expression.U32(0x108))));
            }
            var args = new List<uint>();
            foreach (var input in inputs)
            {
                var parts = input.Parts.Select(p => sampleMasks.Contains(p.Id) ? R(Op.CompositeExtract, p.Type, R(Op.Load, new ShaderType.Array(p.Type, 1), p.Id), 0) : R(Op.Load, p.Type, p.Id)).ToArray();
                args.Add(input.Argument.Type is ShaderType.Structure ? R(Op.CompositeConstruct, input.Argument.Type, parts) : parts[0]);
            }
            uint value = R(Op.FunctionCall, function.ReturnType, new uint[] { functions[function.Name].Id }.Concat(args).ToArray());
            if (function.Stage is ShaderStage.Task or ShaderStage.Mesh)
                EmitMeshFinish(function, id, value, localInvocationIndex!.Value, code, variables, interfaces);
            foreach (var output in outputs)
            {
                uint item = output.Member is uint member ? R(Op.CompositeExtract, output.Type, value, member) : value;
                if (options.AdjustCoordinateSpace && function.Stage == ShaderStage.Vertex && output.Binding.Builtin == "position")
                {
                    uint y = R(Op.CompositeExtract, ShaderType.F32, item, 1), negativeY = R(Op.FNegate, ShaderType.F32, y);
                    item = R(Op.CompositeInsert, output.Type, negativeY, item, 1);
                }
                if (options.ClampFragmentDepth && output.Binding.Builtin == "frag_depth")
                    item = R(Op.ExtInst, output.Type, GlslImport(), 43, item, Constant(new Expression.Literal(0f, ShaderType.F32)), Constant(new Expression.Literal(1f, ShaderType.F32)));
                if (sampleMasks.Contains(output.Id)) item = R(Op.CompositeConstruct, new ShaderType.Array(output.Type, 1), item);
                code.Add(I(Op.Store, output.Id, item));
            }
            if (function.Stage != ShaderStage.Task) code.Add(I(Op.Return));
            bodies.Add(I(Op.Function, Type(new ShaderType.Void()), id, 0, signature)); bodies.Add(I(Op.Label, Id())); bodies.AddRange(variables); bodies.AddRange(code); bodies.Add(I(Op.FunctionEnd));
            uint stage = function.Stage switch { ShaderStage.Vertex => 0, ShaderStage.Fragment => 4, ShaderStage.Compute => 5, ShaderStage.Task => 5364, ShaderStage.Mesh => 5365, _ => throw Error("Missing entry point stage.") };
            if (meshShaderModule)
            {
                var visited = new HashSet<uint>();
                void Collect(uint fn) { if (!visited.Add(fn)) return; interfaces.AddRange(functionGlobalUses[fn]); foreach (uint callee in functionCalls[fn]) Collect(callee); }
                Collect(functions[function.Name].Id);
                if (function.TaskPayload is string payload) interfaces.Add(globals[payload].Id);
            }
            entryPoints.Add(I(Op.EntryPoint, new uint[] { stage, id }.Concat(SpirvBinary.StringWords(function.Name)).Concat(interfaces.Distinct()).ToArray()));
            if (function.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh)
            {
                EmitWorkgroupSize(function, id);
            }
            if (function.Stage == ShaderStage.Fragment)
            {
                executionModes.Add(I(Op.ExecutionMode, id, 7));
                if (outputs.Any(o => o.Binding.Builtin == "frag_depth")) executionModes.Add(I(Op.ExecutionMode, id, 12));
                if (function.EarlyDepthTest) executionModes.Add(I(Op.ExecutionMode, id, 9));
                if (function.ConservativeDepth is string depth) executionModes.Add(I(Op.ExecutionMode, id, depth switch { "greater_equal" => 14u, "less_equal" => 15u, "unchanged" => 16u, _ => throw Error("Invalid conservative depth mode.") }));
            }
        }

        private void IoDecorations(uint id, IoBinding binding, bool input)
        {
            if (binding.Location is uint location) Decorate(id, 30, location);
            if (binding.BlendSource is uint source) Decorate(id, 32, source);
            if (binding.PerPrimitive) { Decorate(id, 5271); capabilities.Add(5283); extensions.Add("SPV_EXT_mesh_shader"); }
            if (binding.Builtin is string builtin)
            {
                if (builtin == "primitive_index" && !meshShaderModule) capabilities.Add(2);
                if (builtin == "sample_index") capabilities.Add(35);
                if (builtin == "clip_distances") capabilities.Add(32);
                if (builtin == "cull_distance") capabilities.Add(33);
                if (builtin == "view_index") { capabilities.Add(4439); extensions.Add("SPV_KHR_multiview"); }
                if (builtin == "draw_index") { capabilities.Add(4427); extensions.Add("SPV_KHR_shader_draw_parameters"); }
                if (builtin is "barycentric" or "barycentric_no_perspective") { capabilities.Add(5284); extensions.Add("SPV_KHR_fragment_shader_barycentric"); }
                if (builtin is "subgroup_id" or "subgroup_size" or "subgroup_invocation_id" or "num_subgroups") capabilities.Add(61);
                if (builtin is "primitive_index" or "sample_index" or "front_facing" or "view_index" && input) Decorate(id, 14);
                Decorate(id, 11, Builtin(builtin, input));
            }
            if (binding.Invariant) Decorate(id, 18);
            if (binding.Interpolation == "flat") Decorate(id, 14);
            if (binding.Interpolation == "linear") Decorate(id, 13);
            if (binding.Interpolation == "per_vertex")
            {
                Decorate(id, 5285); capabilities.Add(5284); extensions.Add("SPV_KHR_fragment_shader_barycentric");
            }
            if (binding.Sampling == "centroid") Decorate(id, 16);
            if (binding.Sampling == "sample") { Decorate(id, 17); capabilities.Add(35); }
        }
        private uint Builtin(string builtin, bool input) => builtin switch
        {
            "position" => input ? 15u : 0u, "point_size" => 1, "clip_distances" => 3, "cull_distance" => 4,
            "vertex_index" => 42, "instance_index" => 43, "primitive_index" => 7, "front_facing" => 17,
            "draw_index" => 4426, "view_index" => 4440, "barycentric" => 5286, "barycentric_no_perspective" => 5287,
            "sample_index" => 18, "sample_mask" => 20, "frag_depth" => 22, "num_workgroups" => 24,
            "workgroup_id" => 26, "local_invocation_id" => 27, "global_invocation_id" => 28, "local_invocation_index" => 29,
            "subgroup_size" => 36, "num_subgroups" => 38, "subgroup_id" => 40, "subgroup_invocation_id" => 41,
            "point_index" => 5294, "line_indices" => 5295, "triangle_indices" => 5296, "cull_primitive" => 5299,
            _ => throw Error($"Unsupported builtin '{builtin}'.")
        };
    }
}

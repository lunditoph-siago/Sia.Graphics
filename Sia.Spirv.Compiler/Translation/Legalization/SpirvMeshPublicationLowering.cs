using System.Collections.ObjectModel;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed record SpirvMeshPublication(ShaderFunction Function, uint Topology, uint MaxVertices, uint MaxPrimitives);

/// <summary>Prepare collective publication and distributed output copies as typed IR.</summary>
internal static class SpirvMeshPublicationLowering
{
    internal static (SpirvPhysicalLayout Layout, IReadOnlyDictionary<string, SpirvMeshPublication> Publications) Run(
        SpirvPhysicalLayout layout, IReadOnlyDictionary<(string Entry, string? Member), ShaderFunction> conversions)
    {
        var input = layout.Module;
        var entries = input.Functions.Where(f => f.Stage is ShaderStage.Task or ShaderStage.Mesh).ToArray();
        var publications = new Dictionary<string, SpirvMeshPublication>(StringComparer.Ordinal);
        if (entries.Length == 0) return (layout, new ReadOnlyDictionary<string, SpirvMeshPublication>(publications));
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        output.Functions.AddRange(input.Functions); output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        var names = input.Functions.Select(f => f.Name).Concat(input.Globals.Select(g => g.Name))
            .Concat(input.Constants.Select(c => c.Name)).Concat(input.Structures.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
        string Fresh(string stem) { string name = stem; int suffix = 0; while (!names.Add(name)) name = stem + "_" + ++suffix; return name; }
        Expression.Reference Ref(string name, ShaderType type) => new(name, type);
        Expression Field(Expression parent, StructMember member) => new Expression.Member(parent, member.Name,
            new ShaderType.Pointer(member.Type, AddressSpace.Workgroup));
        foreach (var entry in entries) {
            var helper = new ShaderFunction(Fresh("sia_spv_mesh_finish_" + entry.Name));
            output.Functions.Add(helper);
            if (entry.Stage == ShaderStage.Task) {
                var dimensions = new ShaderType.Vector(3, ShaderType.U32);
                string argumentName = Fresh("dimensions");
                helper.Arguments.Add(new(argumentName, dimensions));
                helper.Body.Statements.Add(new Statement.TaskDispatch(Ref(argumentName, dimensions), entry.TaskPayload!));
                publications.Add(entry.Name, new(helper, 0, 0, 0));
                continue;
            }
            var info = MeshShaderInfo.Inspect(input, entry);
            string localIndex = Fresh("local_index");
            helper.Arguments.Add(new(localIndex, ShaderType.U32));
            helper.Body.Statements.Add(new Statement.Barrier(false, true));
            var root = Ref(info.Variable.Name, new ShaderType.Pointer(info.Structure, AddressSpace.Workgroup));
            Expression Read(Expression pointer, ShaderType type, MemoryDecorations memory) => new Expression.Load(pointer) {
                MemoryAccess = ShaderMemoryRequirements.Decorate(null, memory | ShaderMemoryRequirements.TypeMemory(type),
                    AddressSpace.Workgroup, input.VulkanMemoryModel || ShaderMemoryRequirements.UsesCooperativeMemoryModel(input))
            };
            Expression Count(int member, uint maximum, string name) {
                var field = info.Structure.Members[member];
                var read = Read(Field(root, field), ShaderType.U32, info.Variable.MemoryDecorations | field.MemoryDecorations);
                var bounded = new Expression.Call("min", [read, Expression.U32(maximum)], ShaderType.U32, CallBinding.Builtin) ;
                helper.Body.Statements.Add(new Statement.Declare(name, ShaderType.U32, bounded, false)); return Ref(name, ShaderType.U32);
            }
            uint maxVertices = info.Vertices.Length!.Value, maxPrimitives = info.Primitives.Length!.Value;
            var vertices = Count(info.VertexCountMember, maxVertices, Fresh("vertex_count"));
            var primitives = Count(info.PrimitiveCountMember, maxPrimitives, Fresh("primitive_count"));
            helper.Body.Statements.Add(new Statement.MeshSetOutputs(vertices, primitives));
            Expression step = new Expression.Convert(ShaderType.U32, entry.WorkgroupSize[0]);
            foreach (var size in entry.WorkgroupSize.Skip(1)) step = new Expression.Binary("*", step, new Expression.Convert(ShaderType.U32, size), ShaderType.U32);
            string stepName = Fresh("step");
            helper.Body.Statements.Add(new Statement.Declare(stepName, ShaderType.U32, step, false));
            void Copy(ShaderType.Array array, int memberIndex, Expression count, bool primitive) {
                string name = Fresh(primitive ? "primitive_index" : "vertex_index");
                var index = Ref(name, ShaderType.U32);
                helper.Body.Statements.Add(new Statement.Declare(name, ShaderType.U32, Ref(localIndex, ShaderType.U32)));
                var body = new Block(); var stop = new Block(); stop.Statements.Add(new Statement.Break());
                body.Statements.Add(new Statement.If(new Expression.Binary(">=", index, count, ShaderType.Bool), stop, new()));
                var structure = (ShaderType.Structure)array.Element;
                var arrayMember = info.Structure.Members[memberIndex];
                var element = new Expression.Access(Field(root, arrayMember), index, new ShaderType.Pointer(structure, AddressSpace.Workgroup));
                foreach (var member in structure.Members) {
                    var physical = layout.WorkgroupTypes[member.Type];
                    Expression value = Read(Field(element, member), member.Type,
                        info.Variable.MemoryDecorations | arrayMember.MemoryDecorations | member.MemoryDecorations);
                    if (!primitive && conversions.TryGetValue((entry.Name, member.Name), out var conversion))
                        value = new Expression.Call(conversion.Name, [value], conversion.ReturnType, CallBinding.Function) ;
                    if (physical != member.Type) {
                        var physicalConversion = layout.WorkgroupConversions[(member.Type, true)];
                        value = new Expression.Call(physicalConversion.Name, [value], physicalConversion.ReturnType, CallBinding.Function) ;
                    }
                    var binding = member.Binding!;
                    var outputField = new MeshOutputField(Fresh(entry.Name + "_" + member.Name), physical, array.Length!.Value,
                        binding, primitive && binding.Builtin is not ("point_index" or "line_indices" or "triangle_indices"));
                    body.Statements.Add(new Statement.MeshStore(outputField, index, value));
                }
                var continuing = new Block();
                continuing.Statements.Add(new Statement.Store(index, new Expression.Binary("+", index, Ref(stepName, ShaderType.U32), ShaderType.U32)));
                helper.Body.Statements.Add(new Statement.Loop(body, continuing));
            }
            Copy(info.Vertices, info.VerticesMember, vertices, false);
            Copy(info.Primitives, info.PrimitivesMember, primitives, true);
            publications.Add(entry.Name, new(helper, info.Topology, maxVertices, maxPrimitives));
        }
        var prepared = SpirvControlFlowLowering.Prepare(layout with { Module = output });
        foreach (var publication in publications.Values)
            if (!prepared.ControlFlow.ContainsKey(publication.Function.Name))
                throw new ShaderException(DiagnosticStage.SpirvWrite, "Mesh publication requires canonical verification: "
                    + prepared.DeferredControlFlow[publication.Function.Name]);
        ModuleValidator.Validate(prepared.Canonical);
        return (prepared, new ReadOnlyDictionary<string, SpirvMeshPublication>(publications));
    }
}

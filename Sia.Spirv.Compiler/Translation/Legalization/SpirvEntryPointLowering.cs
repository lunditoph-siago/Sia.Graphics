using System.Collections.ObjectModel;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Prepare initialization and output conversions at the entry boundary, without rewriting calls to entry bodies.</summary>
internal static class SpirvEntryPointLowering
{
    internal sealed record Result(Module Module, IReadOnlyDictionary<(string Entry, string? Member), ShaderFunction> OutputFunctions,
        IReadOnlyDictionary<string, ShaderFunction> WorkgroupInitializers, SpirvPhysicalLayout PhysicalLayout,
        IReadOnlyDictionary<string, SpirvMeshPublication> MeshPublications);

    public static Result Run(Module input, bool adjustCoordinateSpace, bool clampFragmentDepth, bool zeroInitializeWorkgroupMemory = true, bool useLocalSizeId = false, uint? version = null)
    {
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Globals.AddRange(input.Globals); output.Constants.AddRange(input.Constants);
        output.Functions.AddRange(input.Functions); output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        var conversions = new Dictionary<(string Entry, string? Member), ShaderFunction>();
        var helpers = new Dictionary<string, ShaderFunction>(StringComparer.Ordinal);
        var names = input.Functions.Select(f => f.Name).Concat(input.Globals.Select(g => g.Name))
            .Concat(input.Constants.Select(c => c.Name)).Concat(input.Structures.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);

        ShaderFunction Helper(string policy)
        {
            if (helpers.TryGetValue(policy, out var known)) return known;
            string stem = "sia_spv_output_" + policy, name = stem; int suffix = 0;
            while (!names.Add(name)) name = stem + "_" + ++suffix;
            ShaderType type = policy == "position" ? new ShaderType.Vector(4, ShaderType.F32) : ShaderType.F32;
            var value = new Expression.Reference("value", type);
            Expression result;
            if (policy == "position") {
                Expression Component(string component) => new Expression.Swizzle(value, component, ShaderType.F32);
                result = new Expression.Construct(type, [Component("x"), new Expression.Unary("-", Component("y"), ShaderType.F32), Component("z"), Component("w")]);
            }
            else result = new Expression.Call("clamp", [value, new Expression.Literal(0f, ShaderType.F32), new Expression.Literal(1f, ShaderType.F32)], type) { Binding = CallBinding.Builtin };
            var function = new ShaderFunction(name) { ReturnType = type };
            function.Arguments.Add(new("value", type)); function.Body.Statements.Add(new Statement.Return(result));
            helpers.Add(policy, function); output.Functions.Add(function); return function;
        }

        foreach (var entry in input.Functions.Where(f => f.Stage is not null)) {
            void Output(string? member, IoBinding? binding)
            {
                if (adjustCoordinateSpace && entry.Stage == ShaderStage.Vertex && binding?.Builtin == "position")
                    conversions.Add((entry.Name, member), Helper("position"));
                if (clampFragmentDepth && binding?.Builtin == "frag_depth")
                    conversions.Add((entry.Name, member), Helper("depth"));
            }
            if (entry.ReturnType is ShaderType.Structure structure)
                foreach (var member in structure.Members) Output(member.Name, member.Binding);
            else Output(null, entry.ReturnBinding);
            if (adjustCoordinateSpace && entry.Stage == ShaderStage.Mesh) {
                var mesh = MeshShaderInfo.Inspect(input, entry);
                foreach (var member in ((ShaderType.Structure)mesh.Vertices.Element).Members.Where(m => m.Binding?.Builtin == "position"))
                    conversions.Add((entry.Name, member.Name), Helper("position"));
            }
        }
        var initializers = new Dictionary<string, ShaderFunction>(StringComparer.Ordinal);
        var entries = input.Functions.Where(f => f.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh).ToArray();
        if (zeroInitializeWorkgroupMemory && input.WorkgroupInitializationRequired && entries.Length != 0
            && input.Globals.Any(g => g.Space == AddressSpace.Workgroup)) {
            var initializer = SpirvWorkgroupInitializationLowering.Create(output, names);
            output.Functions.Add(initializer);
            ModuleValidator.Validate(output);
            if (!StructuredControlFlowReader.TryRead(initializer, output, out var graph, out var deferred))
                throw new ShaderException(DiagnosticStage.SpirvWrite, "Workgroup initialization requires canonical verification: " + deferred);
            ControlFlowAnalysis.RemoveUnreachable(graph!);
            ControlFlowVerifier.Validate(graph!, output); LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, output);
            var lowered = StructuredControlFlowLowering.Run(graph!, output);
            output.Functions[^1] = lowered;
            foreach (var entry in entries) initializers.Add(entry.Name, lowered);
        }
        ModuleValidator.Validate(output);
        foreach (var function in helpers.Values) {
            if (!StructuredControlFlowReader.TryRead(function, output, out var graph, out var deferred))
                throw new ShaderException(DiagnosticStage.SpirvWrite, "Entry output legalization requires canonical verification: " + deferred);
            ControlFlowVerifier.Validate(graph!, output); LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, output);
        }
        var layout = SpirvPhysicalLayoutLowering.Prepare(output, useLocalSizeId, version);
        ModuleValidator.Validate(layout.Module);
        var publication = SpirvMeshPublicationLowering.Run(layout, conversions);
        layout = publication.Layout;
        ModuleValidator.Validate(layout.Module);
        layout = SpirvControlFlowLowering.Prepare(layout);
        var finalFunctions = layout.Module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var result = new Result(layout.Module, new ReadOnlyDictionary<(string Entry, string? Member), ShaderFunction>(
                conversions.ToDictionary(p => p.Key, p => finalFunctions[p.Value.Name])),
            new ReadOnlyDictionary<string, ShaderFunction>(initializers.ToDictionary(p => p.Key, p => finalFunctions[p.Value.Name], StringComparer.Ordinal)),
            layout, new ReadOnlyDictionary<string, SpirvMeshPublication>(publication.Publications.ToDictionary(p => p.Key,
                p => p.Value with { Function = finalFunctions[p.Value.Function.Name] }, StringComparer.Ordinal)));
        layout = SpirvEntryWrapperLowering.Prepare(layout, result.OutputFunctions, result.WorkgroupInitializers, result.MeshPublications);
        layout = SpirvEntryMetadataLowering.Prepare(layout, useLocalSizeId, version);
        return result with { PhysicalLayout = layout };
    }
}

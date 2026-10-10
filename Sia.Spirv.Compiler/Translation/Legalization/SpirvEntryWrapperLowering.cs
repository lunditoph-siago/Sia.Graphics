using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed record SpirvEntryWrapper(ShaderFunction Entry, SpirvFunctionControlFlow Function,
    IReadOnlyList<EntryInterfaceField> Interfaces, bool WritesDepth,
    SpirvMeshPublication? Publication);

/// <summary>Prepare the target entry ABI as an ordinary canonical function with explicit IO effects.</summary>
internal static class SpirvEntryWrapperLowering
{
    internal static SpirvPhysicalLayout Prepare(SpirvPhysicalLayout layout,
        IReadOnlyDictionary<(string Entry, string? Member), ShaderFunction>? conversions = null,
        IReadOnlyDictionary<string, ShaderFunction>? initializers = null,
        IReadOnlyDictionary<string, SpirvMeshPublication>? publications = null)
    {
        var module = layout.Module;
        var verification = new Module { VulkanMemoryModel = module.VulkanMemoryModel, WorkgroupInitializationRequired = module.WorkgroupInitializationRequired };
        verification.Structures.AddRange(module.Structures); verification.Globals.AddRange(module.Globals);
        verification.Constants.AddRange(module.Constants); verification.Enables.UnionWith(module.Enables);
        verification.DiagnosticFilters.AddRange(module.DiagnosticFilters);
        // Entry ABI wrappers call entry bodies as target helpers. Source-level
        // prohibition on calling an entry remains unchanged in the source module.
        foreach (var source in module.Functions) {
            var helper = new ShaderFunction(source.Name) { ReturnType = source.ReturnType, Body = source.Body };
            helper.Arguments.AddRange(source.Arguments); helper.DiagnosticFilters.AddRange(source.DiagnosticFilters);
            verification.Functions.Add(helper);
        }
        var effects = ShaderEffectAnalysis.Compute(verification, layout.ControlFlow.ToDictionary(p => p.Key, p => p.Value.Graph, StringComparer.Ordinal));
        var names = module.Functions.Select(f => f.Name).Concat(module.Globals.Select(g => g.Name))
            .Concat(module.Constants.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        var wrappers = new Dictionary<string, SpirvEntryWrapper>(StringComparer.Ordinal);
        foreach (var entry in module.Functions.Where(f => f.Stage is not null)) {
            SpirvMeshPublication? publication = null;
            if (entry.Stage is ShaderStage.Mesh or ShaderStage.Task && publications?.TryGetValue(entry.Name, out publication) != true)
                continue; // Physical preparation precedes mesh publication; the final entry pass fills these wrappers.
            string stem = "sia_spv_entry_" + entry.Name, name = stem; int suffix = 0;
            while (!names.Add(name)) name = stem + "_" + ++suffix;
            var signature = new ShaderFunction(name) { Stage = entry.Stage };
            var graph = new ControlFlowFunction(signature); var block = graph.Block(); graph.Entry = block.Id;
            var fields = new List<EntryInterfaceField>();
            SsaValue Emit(ShaderType type, ValueOperation operation) {
                var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value;
            }
            SsaValue? Call(ShaderFunction function, params SsaValue[] arguments) {
                var operation = new ValueOperation.Call(function.Name, arguments, function.ReturnType, CalleeEffects: effects[function.Name]);
                if (function.ReturnType is ShaderType.Void) { block.Instructions.Add(new(null, operation)); return null; }
                return Emit(function.ReturnType, operation);
            }
            EntryInterfaceField Interface(string fieldName, ShaderType type, IoBinding binding, bool input) {
                if (binding.Builtin == "sample_mask") type = new ShaderType.Array(type, 1);
                var field = new EntryInterfaceField(fieldName, type, binding, input); fields.Add(field); return field;
            }
            SsaValue Read(EntryInterfaceField field, ShaderType logical) {
                var value = Emit(field.Type, new ValueOperation.InterfaceLoad(field));
                return field.Binding.Builtin == "sample_mask"
                    ? Emit(logical, new ValueOperation.Access(value, Emit(ShaderType.U32, new ValueOperation.Literal(0u)))) : value;
            }
            var outputs = new List<(EntryInterfaceField Field, ShaderType Logical, string? Member)>();
            if (entry.ReturnType is not ShaderType.Void && entry.Stage != ShaderStage.Task) {
                if (entry.ReturnType is ShaderType.Structure structure)
                    foreach (var field in structure.Members)
                        outputs.Add((Interface(field.Name, field.Type, field.Binding ?? throw Error("Entry output member has no binding."), false), field.Type, field.Name));
                else outputs.Add((Interface(entry.Name + "_output", entry.ReturnType, entry.ReturnBinding ?? throw Error("Entry output has no binding."), false), entry.ReturnType, null));
            }
            var inputs = new List<(FunctionArgument Argument, IReadOnlyList<EntryInterfaceField> Fields)>();
            EntryInterfaceField? localIndex = null;
            foreach (var argument in entry.Arguments) {
                var parts = new List<EntryInterfaceField>();
                if (argument.Type is ShaderType.Structure structure) {
                    foreach (var member in structure.Members) {
                        var field = Interface(member.Name, member.Type, member.Binding ?? throw Error("Entry input member has no binding."), true);
                        parts.Add(field); if (field.Binding.Builtin == "local_invocation_index") localIndex = field;
                    }
                }
                else {
                    var field = Interface(argument.Name, argument.Type, argument.Binding ?? throw Error("Entry input has no binding."), true);
                    parts.Add(field); if (field.Binding.Builtin == "local_invocation_index") localIndex = field;
                }
                inputs.Add((argument, parts.AsReadOnly()));
            }
            if (entry.Stage is ShaderStage.Mesh or ShaderStage.Task)
                localIndex ??= Interface("sia_mesh_local_invocation_index", ShaderType.U32, new(Builtin: "local_invocation_index"), true);
            if (initializers?.TryGetValue(entry.Name, out var initializer) == true) {
                localIndex ??= Interface("sia_local_invocation_index", ShaderType.U32, new(Builtin: "local_invocation_index"), true);
                Call(initializer, Read(localIndex, ShaderType.U32));
            }
            var arguments = new List<SsaValue>();
            foreach (var input in inputs) {
                var parts = input.Fields.Select((field, index) => Read(field, input.Argument.Type is ShaderType.Structure structure
                    ? structure.Members[index].Type : input.Argument.Type)).ToArray();
                arguments.Add(input.Argument.Type is ShaderType.Structure ? Emit(input.Argument.Type, new ValueOperation.Construct(parts)) : parts[0]);
            }
            var returned = Call(entry, arguments.ToArray());
            if (publication is not null) Call(publication.Function, entry.Stage == ShaderStage.Task ? returned!.Value : Read(localIndex!, ShaderType.U32));
            foreach (var output in outputs) {
                var value = output.Member is { } member ? Emit(output.Logical, new ValueOperation.Member(returned!.Value, member)) : returned!.Value;
                if (conversions?.TryGetValue((entry.Name, output.Member), out var conversion) == true) value = Call(conversion, value)!.Value;
                if (output.Field.Binding.Builtin == "sample_mask") value = Emit(output.Field.Type, new ValueOperation.Construct([value]));
                block.Instructions.Add(new(null, new ValueOperation.InterfaceStore(output.Field, value)));
            }
            block.Terminator = entry.Stage == ShaderStage.Task ? new ControlFlowTerminator.Unreachable() : new ControlFlowTerminator.Return();
            ControlFlowVerifier.Validate(graph, verification, calleeEffects: effects);
            wrappers.Add(entry.Name, new(entry, SpirvControlFlowLowering.Prepare(graph, verification, effects), fields.AsReadOnly(),
                outputs.Any(o => o.Field.Binding.Builtin == "frag_depth"), publication));
        }
        return layout with { EntryWrappers = wrappers.ToFrozenDictionary(StringComparer.Ordinal) };
    }

    private static ShaderException Error(string message) => new(DiagnosticStage.SpirvWrite, message);
}

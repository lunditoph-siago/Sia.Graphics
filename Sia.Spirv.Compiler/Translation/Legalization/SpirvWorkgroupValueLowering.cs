using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Prepare pure aggregate conversions independently of ordered memory accesses.</summary>
internal static class SpirvWorkgroupValueLowering
{
    internal static SpirvPhysicalLayout Run(SpirvPhysicalLayout layout, IReadOnlyList<ShaderType> typeOrder,
        IDictionary<string, ControlFlowFunction>? ownedGraphs = null)
    {
        var conversions = new Dictionary<(ShaderType Logical, bool ToPhysical), ShaderFunction>();
        var input = layout.Module;
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Globals.AddRange(input.Globals); output.Constants.AddRange(input.Constants);
        output.Functions.AddRange(input.Functions); output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        var names = input.Functions.Select(f => f.Name).Concat(input.Globals.Select(g => g.Name))
            .Concat(input.Constants.Select(c => c.Name)).Concat(input.Structures.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);

        Expression Convert(Expression value, ShaderType logical, bool toPhysical)
        {
            ShaderType physical = layout.WorkgroupTypes[logical];
            if (logical == physical) return value;
            ShaderType result = toPhysical ? physical : logical;
            if (logical is ShaderType.Array array && physical is ShaderType.Array mapped && array.Length is uint length) {
                ShaderType element = toPhysical ? array.Element : mapped.Element;
                return new Expression.Construct(result, Enumerable.Range(0, checked((int)length))
                    .Select(i => Convert(new Expression.Access(value, Expression.U32((uint)i), element), array.Element, toPhysical)).ToArray());
            }
            if (logical is ShaderType.Structure structure && physical is ShaderType.Structure mappedStructure) {
                var members = toPhysical ? structure.Members : mappedStructure.Members;
                return new Expression.Construct(result, members.Select((m, i) => Convert(new Expression.Member(value, m.Name, m.Type),
                    structure.Members[i].Type, toPhysical)).ToArray());
            }
            throw new ShaderException(DiagnosticStage.SpirvWrite, "Incompatible workgroup layout conversion.");
        }

        // Pending-length arrays and atomic-containing aggregates are not first-class
        // data values. Their element/atomic accesses keep their own ordered operations.
        // Use frontend encounter order, not hash-based frozen dictionary enumeration.
        foreach (var logical in typeOrder.Where(t => t != layout.WorkgroupTypes[t] && CanonicalTypes.Data(t))) {
            ShaderType physical = layout.WorkgroupTypes[logical];
            foreach (bool toPhysical in new[] { true, false }) {
                string stem = "sia_spv_workgroup_" + (toPhysical ? "to_" : "from_") + conversions.Count, name = stem; int suffix = 0;
                while (!names.Add(name)) name = stem + "_" + ++suffix;
                ShaderType argument = toPhysical ? logical : physical, result = toPhysical ? physical : logical;
                var helper = new ShaderFunction(name) { ReturnType = result };
                helper.Arguments.Add(new("value", argument));
                helper.Body.Statements.Add(new Statement.Return(Convert(new Expression.Reference("value", argument), logical, toPhysical)));
                output.Functions.Add(helper); conversions.Add((logical, toPhysical), helper);
            }
        }
        if (conversions.Count == 0) return layout;
        foreach (var helper in conversions.Values) {
            if (!StructuredControlFlowReader.TryRead(helper, output, out var graph, out var deferred))
                throw new ShaderException(DiagnosticStage.SpirvWrite, "Workgroup value conversion requires canonical verification: " + deferred);
            ControlFlowVerifier.Validate(graph!, output); LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, output);
            ownedGraphs?.Add(helper.Name, graph!);
        }
        return layout with { Module = output, WorkgroupConversions = conversions.ToFrozenDictionary() };
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static partial class CanonicalReferenceLowering
{
    // After the complete private-slot call closure expands, an entry owns one
    // allocation per invocation. Repeated former helper calls share that storage.
    private static CanonicalModule LocalizePrivateSlots(CanonicalModule input)
    {
        var globals = input.Declarations.Globals.Where(g => g.Space == AddressSpace.Private && g.Type is ShaderType.Pointer)
            .ToDictionary(g => g.Name, StringComparer.Ordinal);
        if (globals.Count == 0) return input;
        ShaderException Error(string detail) => new(DiagnosticStage.Validation, "Canonical private pointer slots: " + detail);
        if (input.DeferredFunctions.Count != 0) throw Error("deferred functions require prior slot-call migration");
        if (globals.Values.Any(g => g.Initializer is not null)) throw Error("initializer requires an explicit stored address");
        var called = input.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions)
            .Select(i => i.Operation).OfType<ValueOperation.Call>().Select(c => c.Function).ToHashSet(StringComparer.Ordinal);
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var (name, original) in input.Functions) {
            var symbols = original.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Symbol s && globals.ContainsKey(s.Name)).ToArray();
            if (symbols.Length == 0) { graphs.Add(name, original); continue; }
            if (called.Contains(name)) throw Error("private slot users must expand into their invocation owner");
            var graph = original.Copy(); var entry = graph.Blocks.Single(b => b.Id == graph.Entry);
            var locations = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
            foreach (var symbol in symbols.Select(i => ((ValueOperation.Symbol)i.Operation).Name).Distinct()) {
                var slot = graph.Value(new ShaderType.Pointer(globals[symbol].Type, AddressSpace.Function));
                locations.Add(symbol, slot);
                entry.Instructions.Insert(0, new(slot, new ValueOperation.Local(symbol, false)));
            }
            var replacements = symbols.ToDictionary(i => i.Result!.Value.Id, i => locations[((ValueOperation.Symbol)i.Operation).Name]);
            SsaValue Value(SsaValue value) => replacements.TryGetValue(value.Id, out var slot) ? slot
                : value.Type is ShaderType.Pointer { Space: AddressSpace.Private, Base: ShaderType.Pointer } pointer
                    ? value with { Type = pointer with { Space = AddressSpace.Function } } : value;
            foreach (var block in graph.Blocks) {
                for (int i = 0; i < block.Parameters.Count; i++) block.Parameters[i] = Value(block.Parameters[i]);
                block.Instructions.RemoveAll(i => i.Result is { } result && replacements.ContainsKey(result.Id));
                for (int i = 0; i < block.Instructions.Count; i++) {
                    var instruction = block.Instructions[i];
                    block.Instructions[i] = instruction with { Result = instruction.Result is { } result ? Value(result) : null, Operation = instruction.Operation.Map(Value) };
                }
                block.Terminator = block.Terminator!.Map(Value);
                foreach (var edge in block.Terminator.Edges)
                    for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Value(edge.Arguments[i]);
            }
            graphs.Add(name, graph);
        }
        var source = input.Declarations;
        var module = new Module { VulkanMemoryModel = source.VulkanMemoryModel, WorkgroupInitializationRequired = source.WorkgroupInitializationRequired };
        module.Structures.AddRange(source.Structures); module.Constants.AddRange(source.Constants); module.Functions.AddRange(source.Functions);
        module.Globals.AddRange(source.Globals.Where(g => !globals.ContainsKey(g.Name)));
        module.Enables.UnionWith(source.Enables); module.DiagnosticFilters.AddRange(source.DiagnosticFilters);
        var output = input with { Declarations = module, Functions = graphs };
        ShaderEffectAnalysis.RefreshCalls(output); ModuleValidator.Validate(output, native: true); return output;
    }
}

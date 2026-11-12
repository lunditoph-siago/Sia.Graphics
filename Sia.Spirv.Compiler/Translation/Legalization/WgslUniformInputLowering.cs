using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static class WgslUniformInputLowering
{
    internal static CanonicalModule Run(CanonicalModule input)
    {
        var entries = input.Functions.Values.Where(g => g.Signature.Stage is not null).ToArray();
        if (entries.Length != 1 || input.Functions.Count != input.Declarations.Functions.Count) return input;
        if (!input.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions)
            .Any(i => (i.Effects & ShaderEffects.Convergent) != 0)) return input;
        var entry = entries[0];
        var eligible = UniformityAnalysis.UniformPrivateGlobals(input.Declarations, input.Functions);
        var definitions = entry.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null)
            .ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
        var copies = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var writes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var instruction in entry.Blocks.SelectMany(b => b.Instructions))
            if (instruction.Operation is ValueOperation.Store store && definitions.GetValueOrDefault(store.Pointer.Id) is ValueOperation.Symbol symbol)
                writes[symbol.Name] = writes.GetValueOrDefault(symbol.Name) + 1;
        foreach (var instruction in entry.Blocks.Single(b => b.Id == entry.Entry).Instructions) {
            if ((instruction.Effects & (ShaderEffects.ReadMemory | ShaderEffects.UnknownCall)) != 0) break;
            if (instruction.Operation is not ValueOperation.Store { MemoryAccess: null } store
                || definitions.GetValueOrDefault(store.Pointer.Id) is not ValueOperation.Symbol symbol
                || !eligible.Contains(symbol.Name) || writes.GetValueOrDefault(symbol.Name) != 1
                || !input.Declarations.Globals.Any(g => g.Name == symbol.Name && g.MemoryDecorations == MemoryDecorations.None)) continue;
            copies.Add(symbol.Name, store.Value);
        }
        if (copies.Count == 0) return input;
        var helpers = input.Functions.Where(p => p.Value.Signature.Stage is null && p.Value.Blocks.SelectMany(b => b.Instructions)
            .Any(i => i.Operation is ValueOperation.Symbol symbol && copies.ContainsKey(symbol.Name)))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do {
            changed = false;
            foreach (var graph in input.Functions.Values.Where(g => g.Signature.Stage is null))
                if (graph.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Call call && helpers.Contains(call.Function)))
                    changed |= helpers.Add(graph.Signature.Name);
        } while (changed);
        var output = TargetHelperLowering.Run(input, additionalHelpers: helpers);
        var promoted = output.Functions[entry.Signature.Name].Copy();
        var allDefinitions = promoted.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null)
            .ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
        foreach (var block in promoted.Blocks)
            for (int index = 0; index < block.Instructions.Count; index++) {
                var instruction = block.Instructions[index];
                if (instruction.Operation is ValueOperation.Load { MemoryAccess: null } load
                    && allDefinitions.GetValueOrDefault(load.Pointer.Id) is ValueOperation.Symbol symbol
                    && copies.TryGetValue(symbol.Name, out var value))
                    block.Instructions[index] = instruction with { Operation = new ValueOperation.Let("sia_uniform_input", value) };
            }
        DeadValueElimination.Run(promoted);
        var graphs = output.Functions.ToDictionary(p => p.Key, p => p.Key == promoted.Signature.Name ? promoted : p.Value, StringComparer.Ordinal);
        var result = output with { Functions = graphs };
        ModuleValidator.Validate(result, native: true);
        return result;
    }
}

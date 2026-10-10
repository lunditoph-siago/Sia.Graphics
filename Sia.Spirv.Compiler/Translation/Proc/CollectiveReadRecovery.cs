using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Prove isolated native barrier/read/barrier regions before recovering a collective read.</summary>
internal static class CollectiveReadRecovery
{
    private sealed record Candidate(string Function, int Block, int Start, int Read, int End, int Value);
    private static bool Barrier(ControlFlowInstruction instruction) => instruction.Operation is ValueOperation.Barrier {
        Control: true, Workgroup: true, Storage: false, Texture: false, Subgroup: false,
        NativeMemory: { Scope: 2, Semantics: 264, ExecutionScope: 2 }
    };
    private static bool Read(ControlFlowInstruction instruction) => instruction.Operation is ValueOperation.Load {
        MemoryAccess: null, Pointer.Type: ShaderType.Pointer { Space: AddressSpace.Workgroup, Base: var type }
    } && instruction.Result is { } result && result.Type == type && CanonicalTypes.Data(type);

    internal static IReadOnlyDictionary<string, ControlFlowFunction> Recover(Module module, IReadOnlyDictionary<string, ControlFlowFunction> graphs)
    {
        var candidates = new Dictionary<(string Function,int Value),Candidate>();
        foreach (var graph in graphs.Values) foreach (var block in graph.Blocks) {
            for (int start = 0; start < block.Instructions.Count; start++) {
                if (!Barrier(block.Instructions[start])) continue;
                int read = -1;
                for (int end = start+1; end < block.Instructions.Count; end++) {
                    var instruction = block.Instructions[end];
                    if (Barrier(instruction)) {
                        if (read >= 0) {
                            int value = block.Instructions[read].Result!.Value.Id;
                            candidates.Add((graph.Signature.Name,value),new(graph.Signature.Name,block.Id,start,read,end,value));
                            start = end; // Do not share a barrier between recovered regions.
                        }
                        break;
                    }
                    if (read < 0 && Read(instruction)) read = end;
                    else if (instruction.Effects != ShaderEffects.None) break;
                }
            }
        }
        if (candidates.Count == 0) return graphs;
        while (candidates.Count != 0) {
            // Keep original barriers during proof. Callee requirement origins survive actual argument binding.
            var hypothesis = Rewrite(graphs,candidates.Values,false);
            foreach (var graph in hypothesis.Values) ControlFlowVerifier.Validate(graph,module);
            var divergent = new HashSet<(string Function,int Value)>();
            _ = UniformityAnalysis.AnalyzeCore(module,hypothesis,divergent);
            bool removed = false;
            foreach (var site in divergent) removed |= candidates.Remove(site);
            if (!removed) {
                var recovered = Rewrite(graphs,candidates.Values,true);
                foreach (var graph in recovered.Values) ControlFlowVerifier.Validate(graph,module);
                return recovered;
            }
            // Removing one unproved uniform result can invalidate pointers/control of later candidates.
        }
        return graphs;
    }

    private static IReadOnlyDictionary<string,ControlFlowFunction> Rewrite(IReadOnlyDictionary<string,ControlFlowFunction> graphs,
        IEnumerable<Candidate> candidates,bool removeBarriers)
    {
        var output = graphs.ToDictionary(p => p.Key,p => p.Value,StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(c => c.Function)) {
            var graph = graphs[group.Key].Copy();
            foreach (var block in graph.Blocks) {
                var selected = group.Where(c => c.Block == block.Id).ToArray();
                if (selected.Length == 0) continue;
                var reads = selected.ToDictionary(c => c.Read);
                var barriers = selected.SelectMany(c => new[] {c.Start,c.End}).ToHashSet();
                var instructions = block.Instructions.ToArray(); block.Instructions.Clear();
                for (int i = 0; i < instructions.Length; i++) {
                    if (removeBarriers && barriers.Contains(i)) continue;
                    var instruction = instructions[i];
                    if (reads.ContainsKey(i)) {
                        var load = (ValueOperation.Load)instruction.Operation;
                        instruction = instruction with { Operation = new ValueOperation.Builtin("workgroupUniformLoad",[load.Pointer],instruction.Result!.Value.Type) };
                    }
                    block.Instructions.Add(instruction);
                }
            }
            output[group.Key] = graph;
        }
        return output;
    }

    internal static Module Run(Module input)
    {
        var parsed = new Dictionary<string,ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var function in input.Functions)
            if (StructuredControlFlowReader.TryRead(function,input,out var graph,out _)) {
                ControlFlowAnalysis.RemoveUnreachable(graph!); parsed.Add(function.Name,graph!);
            }
        var graphs = UniformityAnalysis.PrepareGraphs(input,parsed);
        var recovered = Recover(input,graphs);
        if (ReferenceEquals(recovered,graphs)) return input;
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions)
            output.Functions.Add(recovered.TryGetValue(function.Name,out var graph) && !ReferenceEquals(graph,graphs[function.Name])
                ? StructuredControlFlowLowering.Run(graph,input) : function);
        ModuleValidator.Validate(output);
        return output;
    }
}

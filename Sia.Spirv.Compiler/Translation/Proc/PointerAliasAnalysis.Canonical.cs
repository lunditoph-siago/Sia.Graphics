using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static partial class PointerAliasAnalysis
{
    internal static Dictionary<int, HashSet<Root>> Origins(Module module, ControlFlowFunction graph)
        => CanonicalAnalysis.ComputeOrigins(module, graph);
    /// <summary>SSA address roots, including loop/merge edge arguments. No target reconstruction is needed.</summary>
    private sealed class CanonicalAnalysis(Module module, ControlFlowFunction graph, Func<string, Summary> callee,
        DiagnosticStage stage)
    {
        private readonly Dictionary<int, HashSet<Root>> origins = ComputeOrigins(module, graph);
        private readonly HashSet<Root> reads = []; private readonly HashSet<Root> writes = [];

        private HashSet<Root> Roots(SsaValue value) => origins.GetValueOrDefault(value.Id) ?? [];

        internal static Dictionary<int, HashSet<Root>> ComputeOrigins(Module module, ControlFlowFunction graph)
        {
            var origins = new Dictionary<int, HashSet<Root>>();
            HashSet<Root> Roots(SsaValue value) => origins.GetValueOrDefault(value.Id) ?? [];
            var blocks = graph.Blocks.ToDictionary(b => b.Id);
            var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
            var arguments = graph.Signature.Arguments.Select((a, i) => (a.Name, Index: i)).ToDictionary(a => a.Name, a => a.Index, StringComparer.Ordinal);
            var globals = module.Globals.Where(g => g.Space != AddressSpace.Handle).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var value in graph.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value))))
                if (value.Type is ShaderType.Pointer) origins.Add(value.Id, []);
            bool changed;
            do {
                changed = false;
                bool Add(SsaValue value, IEnumerable<Root> roots) {
                    if (!origins.TryGetValue(value.Id, out var current)) return false;
                    int before = current.Count; current.UnionWith(roots); return current.Count != before;
                }
                foreach (var instruction in instructions) {
                    if (instruction.Result is not { Type: ShaderType.Pointer } result) continue;
                    IEnumerable<Root> roots = instruction.Operation switch {
                        ValueOperation.Local => [new(Local: result.Id + 1)],
                        ValueOperation.Symbol s when arguments.TryGetValue(s.Name, out int parameter) => [new(parameter)],
                        ValueOperation.Symbol s when globals.Contains(s.Name) => [new(Global: s.Name)],
                        ValueOperation.Let l => Roots(l.Value),
                        ValueOperation.Access a => Roots(a.Base),
                        ValueOperation.Member m => Roots(m.Base),
                        ValueOperation.Swizzle s => Roots(s.Vector),
                        ValueOperation.Select s => Roots(s.Accept).Concat(Roots(s.Reject)),
                        _ => []
                    };
                    changed |= Add(result, roots);
                }
                foreach (var edge in graph.Blocks.SelectMany(b => b.Terminator!.Edges)) {
                    var target = blocks[edge.Target];
                    for (int i = 0; i < target.Parameters.Count; i++)
                        if (target.Parameters[i].Type is ShaderType.Pointer) changed |= Add(target.Parameters[i], Roots(edge.Arguments[i]));
                }
            } while (changed);
            return origins;
        }

        public Summary Run()
        {
            var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
            foreach (var instruction in instructions) {
                switch (instruction.Operation) {
                    case ValueOperation.Load load: reads.UnionWith(Roots(load.Pointer)); break;
                    case ValueOperation.Store store: writes.UnionWith(Roots(store.Pointer)); break;
                    case ValueOperation.Call call:
                        ApplyCall(graph.Signature.Name, call.Function, call.Arguments.Select(a => a.Type is ShaderType.Pointer).ToArray(),
                            call.Arguments.Select(Roots).ToArray(), callee(call.Function), reads, writes, stage, instruction.Span); break;
                    case ValueOperation.Builtin builtin:
                        var effects = PointerEffects(builtin.Function);
                        foreach (var argument in builtin.Arguments.Where(a => a.Type is ShaderType.Pointer)) {
                            if ((effects & ShaderEffects.ReadMemory) != 0) reads.UnionWith(Roots(argument));
                            if ((effects & ShaderEffects.WriteMemory) != 0) writes.UnionWith(Roots(argument));
                        }
                        break;
                }
            }
            return new(reads.Where(r => r.Local == 0).ToHashSet(), writes.Where(r => r.Local == 0).ToHashSet());
        }
    }
}

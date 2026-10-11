using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static partial class PointerAliasAnalysis
{
    internal static bool IsZero(SsaValue value, IReadOnlyDictionary<int, ControlFlowInstruction> definitions)
        => definitions.GetValueOrDefault(value.Id)?.Operation switch {
            ValueOperation.Literal literal => literal.Value is 0 or 0u or 0L or 0UL or (short)0 or (ushort)0,
            ValueOperation.Construct { Components.Count: 0 } => true,
            ValueOperation.Let alias => IsZero(alias.Value, definitions),
            ValueOperation.Convert conversion => IsZero(conversion.Operand, definitions),
            _ => false
        };

    // Immediate containing object of each address, distinct from allocation
    // identity. No index values or target tags are reconstructed by this analysis.
    internal static Dictionary<int, HashSet<ShaderType?>> Containers(Module module, ControlFlowFunction graph)
    {
        var roots = Origins(module, graph);
        var containers = roots.Keys.ToDictionary(id => id, _ => new HashSet<ShaderType?>());
        var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
        var stores = instructions.Select(i => i.Operation).OfType<ValueOperation.Store>().ToArray();
        var blocks = graph.Blocks.ToDictionary(b => b.Id);
        IEnumerable<ShaderType?> Of(SsaValue value) => containers.GetValueOrDefault(value.Id) ?? [];
        bool changed;
        do {
            changed = false;
            void Add(SsaValue value, IEnumerable<ShaderType?> incoming) {
                if (!containers.TryGetValue(value.Id, out var current)) return;
                int before = current.Count; current.UnionWith(incoming); changed |= before != current.Count;
            }
            foreach (var instruction in instructions) {
                if (instruction.Result is not { Type: ShaderType.Pointer } result) continue;
                IEnumerable<ShaderType?> incoming = instruction.Operation switch {
                    ValueOperation.Symbol or ValueOperation.Local or ValueOperation.Construct { Components.Count: 0 } => [null],
                    ValueOperation.Let alias => Of(alias.Value),
                    ValueOperation.Access access => [((ShaderType.Pointer)access.Base.Type).Base],
                    ValueOperation.Member member => [((ShaderType.Pointer)member.Base.Type).Base],
                    ValueOperation.Binary { Operator: "+" } arithmetic => Of(arithmetic.Left),
                    ValueOperation.Load load when load.Pointer.Type is ShaderType.Pointer { Base: ShaderType.Pointer }
                        => stores.Where(s => roots[s.Pointer.Id].Overlaps(roots[load.Pointer.Id])).SelectMany(s => Of(s.Value)).ToArray(),
                    _ => []
                };
                Add(result, incoming);
            }
            foreach (var edge in graph.Blocks.SelectMany(b => b.Terminator!.Edges))
                for (int i = 0; i < edge.Arguments.Count; i++) Add(blocks[edge.Target].Parameters[i], Of(edge.Arguments[i]));
        } while (changed);
        return containers;
    }
}

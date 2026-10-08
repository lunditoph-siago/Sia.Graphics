using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Front;

public static partial class SpirvReader
{
    private sealed partial class PointerPhiLowering
    {
        private readonly Dictionary<uint, uint> definitionOwners = [];
        private readonly Dictionary<uint, PointerSlot> slots = [];
        private readonly Dictionary<uint, PointerSlot> slotAddresses = [];
        private readonly List<SpirvInstruction> addedSlotGlobals = [];
        private sealed class PointerSlot(SpirvInstruction variable)
        {
            public SpirvInstruction Variable { get; } = variable;
            public HashSet<uint> Sources { get; } = [];
            public HashSet<PointerSlot> Incoming { get; } = [];
            public HashSet<PointerSlot> Related { get; } = [];
            public Dictionary<string, Shape> Shapes { get; } = new(StringComparer.Ordinal);
            public Dictionary<(string Shape, int Position), uint> Indices { get; } = [];
            public uint Selector => Variable.Operands[1];
            public uint Space;
            public bool Active;
        }
        private uint SlotRoot(uint address, HashSet<uint> visited)
        {
            if (!visited.Add(address) || !definitions.TryGetValue(address, out var i)) return 0;
            var a = i.Operands;
            if (!IsPointer(a[0]) || !IsPointer(types[a[0]].Operands[2])) return 0;
            return (Op)i.Opcode switch
            {
                Op.Variable or Op.FunctionParameter => address,
                Op.CopyObject when a.Length == 3 => SlotRoot(a[2], visited),
                Op.AccessChain or Op.InBoundsAccessChain when a.Length == 3 => SlotRoot(a[2], visited),
                _ => 0
            };
        }
        private PointerSlot SlotFor(uint address, SpirvInstruction load)
        {
            if (!slotAddresses.TryGetValue(address, out var slot)) throw Error("Loaded pointer requires pointer-slot alias specialization.", load);
            void Activate(PointerSlot value)
            {
                if (value.Active) return;
                var i = value.Variable; var a = i.Operands;
                if (value.Space is not (6 or 7) || (Op)i.Opcode == Op.Variable && (a.Length is < 3 or > 4 || a[2] != value.Space))
                    throw Error("Pointer slots require Function or Private storage.", i);
                value.Active = true;
                foreach (var related in value.Related) Activate(related);
            }
            Activate(slot);
            return slot;
        }
        private static IEnumerable<uint> SlotValues(PointerSlot slot)
        {
            var visited = new HashSet<PointerSlot>();
            IEnumerable<uint> Visit(PointerSlot value)
            {
                if (!visited.Add(value)) yield break;
                foreach (uint source in value.Sources) yield return source;
                foreach (var parent in value.Incoming) foreach (uint source in Visit(parent)) yield return source;
            }
            return Visit(slot).Distinct();
        }
        private IEnumerable<uint> NodeSources(SpirvInstruction instruction) => (Op)instruction.Opcode switch
        {
            Op.Load => SlotValues(SlotFor(instruction.Operands[2], instruction)),
            Op.FunctionParameter => ParameterSources(instruction),
            Op.Function => ReturnSources(instruction),
            Op.FunctionCall => [instruction.Operands[2]],
            _ => Sources(instruction)
        };
        private bool ActiveSlot(uint id, out PointerSlot slot) => slotAddresses.TryGetValue(id, out slot!) && slot.Active;

        private void AllocateSlots()
        {
            foreach (var load in nodes.Values.Where(n => (Op)n.Instruction.Opcode == Op.Load))
            {
                var slot = SlotFor(load.Instruction.Operands[2], load.Instruction);
                uint expected = types[slot.Variable.Operands[0]].Operands[2];
                if (!types[expected].Operands.AsSpan(1).SequenceEqual(types[load.Instruction.Operands[0]].Operands.AsSpan(1)))
                    throw Error("Pointer-slot load result type mismatch.", load.Instruction);
                foreach (var shape in load.Shapes.Values)
                {
                    if (shape.Root == 0) continue;
                    uint owner = definitionOwners[shape.Root];
                    if (owner != 0 && owner != definitionOwners[load.Instruction.Operands[1]])
                        throw Error("Cross-function pointer-slot roots require provenance specialization.", load.Instruction);
                }
            }
            foreach (var slot in slots.Values.Where(s => s.Active))
            {
                var visited = new HashSet<PointerSlot>(); var domain = new Dictionary<string, Shape>();
                void Collect(PointerSlot value)
                {
                    if (!visited.Add(value)) return;
                    foreach (uint source in value.Sources) foreach (var shape in nodes[source].Shapes.Values) domain.TryAdd(shape.Key, shape);
                    foreach (var related in value.Related) Collect(related);
                }
                Collect(slot);
                foreach (var shape in domain.Values.OrderBy(s => s.Tag)) slot.Shapes.Add(shape.Key, shape);
                foreach (var shape in slot.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++)
                        if (shape.Parts[p].Constant == 0) slot.Indices.Add((shape.Key, p), Id());
            }
        }
        private void UpdateSlotInterfaces(List<SpirvInstruction> output)
        {
            if (binary.Version < 0x10400 || addedSlotGlobals.Count == 0) return;
            uint[] globals = addedSlotGlobals.Select(i => i.Operands[1]).ToArray();
            for (int p = 0; p < output.Count; p++)
            {
                var i = output[p]; if ((Op)i.Opcode != Op.EntryPoint) continue;
                var a = i.Operands;
                if (a.Length < 3) throw Error("Invalid entry point operands.", i);
                _ = SpirvBinary.ReadString(a.AsSpan(2), out int words);
                var listed = a[(2 + words)..].ToHashSet();
                output[p] = i with { Operands = [.. a, .. globals.Where(listed.Add)] };
            }
        }
        private bool RewriteSlotInstruction(SpirvInstruction i, List<SpirvInstruction> output)
        {
            var a = i.Operands; Op op = (Op)i.Opcode;
            if (op == Op.Variable && a.Length >= 2 && ActiveSlot(a[1], out var variable))
            {
                uint Initial(Func<Node, uint> value) => a.Length == 4 ? value(nodes[a[3]]) : 0;
                void Declare(uint type, uint id, uint initial)
                {
                    uint pointer = FindType(Op.TypePointer, [variable.Space, type]);
                    Add(variable.Space == 6 ? addedSlotGlobals : output, i, Op.Variable,
                        initial == 0 ? [pointer, id, variable.Space] : [pointer, id, variable.Space, initial]);
                }
                Declare(uintType, a[1], Initial(n => n.Selector));
                foreach (var shape in variable.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Declare(shape.Parts[p].Type, variable.Indices[(shape.Key, p)], Initial(n => Slot(n, shape, p)));
                return true;
            }
            if (op is Op.CopyObject or Op.AccessChain or Op.InBoundsAccessChain && a.Length >= 3 && ActiveSlot(a[1], out var alias))
            {
                Add(output, i, Op.CopyObject, FindType(Op.TypePointer, [alias.Space, uintType]), a[1], a[2]); return true;
            }
            if (op == Op.CopyMemory && a.Length >= 2 && ActiveSlot(a[0], out var destination))
            {
                var source = SlotFor(a[1], i);
                Add(output, i, Op.CopyMemory, [destination.Selector, source.Selector, .. a[2..]]);
                foreach (var shape in destination.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Add(output, i, Op.CopyMemory, [destination.Indices[(shape.Key, p)], source.Indices[(shape.Key, p)], .. a[2..]]);
                return true;
            }
            if (op == Op.Store && a.Length >= 2 && ActiveSlot(a[0], out var target))
            {
                var source = nodes[a[1]];
                Add(output, i, Op.Store, [target.Selector, source.Selector, .. a[2..]]);
                foreach (var shape in target.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Add(output, i, Op.Store, [target.Indices[(shape.Key, p)], Slot(source, shape, p), .. a[2..]]);
                return true;
            }
            if (op == Op.Load && a.Length >= 3 && nodes.TryGetValue(a[1], out var load))
            {
                var slot = slotAddresses[a[2]];
                Add(output, i, Op.Load, [uintType, load.Selector, slot.Selector, .. a[3..]]);
                foreach (var shape in load.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Add(output, i, Op.Load, [shape.Parts[p].Type, Slot(load, shape, p), slot.Indices[(shape.Key, p)], .. a[3..]]);
                Materialize(load, output); return true;
            }
            return false;
        }
    }
}

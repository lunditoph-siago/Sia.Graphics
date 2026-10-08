using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Front;

public static partial class SpirvReader
{
    private sealed partial class PointerPhiLowering
    {
        private sealed record DescriptorValue(string Key, uint Owner, bool Invariant);
        private readonly Dictionary<uint, DescriptorValue> descriptorValues = [];
        private readonly HashSet<uint> descriptorValuesInProgress = [];
        private readonly Dictionary<(uint Node, string Shape), HashSet<DescriptorValue>> descriptorDomains = [];
        private readonly Dictionary<uint, string> descriptorSnapshots = [];
        private readonly HashSet<uint> repeatedValues = [];
        private readonly Dictionary<SpirvInstruction, uint> descriptorInstructionBlocks = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SpirvInstruction, int> descriptorInstructionOrder = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, uint> descriptorBlockOwners = [];
        private readonly Dictionary<uint, HashSet<uint>> descriptorDominators = [];
        private bool WithinDescriptorScope(uint function, uint owner, HashSet<uint>? visited = null)
        {
            if (function == owner) return true;
            visited ??= [];
            if (!visited.Add(function)) return false;
            var callers = binary.Instructions.Where(i => (Op)i.Opcode == Op.FunctionCall && i.Operands[2] == function)
                .Select(i => definitionOwners[i.Operands[1]]).Distinct().ToArray();
            return callers.Length != 0 && callers.All(caller => WithinDescriptorScope(caller, owner, new(visited)));
        }
        private DescriptorValue ScalarDescriptor(uint id)
        {
            if (descriptorValues.TryGetValue(id, out var cached)) return cached;
            if (!definitions.TryGetValue(id, out var i) || !types.TryGetValue(i.Operands[0], out var type) || (Op)type.Opcode != Op.TypeInt)
                throw Error("Descriptor index identity requires a defined integer scalar.", i ?? binary.Instructions[0]);
            if (!descriptorValuesInProgress.Add(id)) throw Error("Cyclic descriptor index identity.", i);
            var a = i.Operands; var op = (Op)i.Opcode;
            DescriptorValue value;
            if (op == Op.CopyObject) value = ScalarDescriptor(a[2]);
            else if (op is Op.Constant or Op.ConstantNull)
            {
                ulong bits = op == Op.ConstantNull ? 0 : a[2] | (a.Length > 3 ? (ulong)a[3] << 32 : 0);
                uint width = types[a[0]].Operands[1]; if (width < 64) bits &= (1UL << (int)width) - 1;
                value = new("n:" + bits, 0, true);
            }
            else if (op == Op.SpecConstantOp && a.Length == 5 && a[2] == (uint)Op.IAdd
                && (ScalarDescriptor(a[3]).Key == "n:0" || ScalarDescriptor(a[4]).Key == "n:0"))
                value = ScalarDescriptor(ScalarDescriptor(a[3]).Key == "n:0" ? a[4] : a[3]);
            else if (constants.Contains(id)) value = new("s:" + id, 0, true);
            else value = new("v:" + id, definitionOwners[id], !repeatedValues.Contains(id));
            descriptorValuesInProgress.Remove(id); descriptorValues.Add(id, value); descriptorSnapshots[id] = value.Key; return value;
        }
        private void FindRepeatedDescriptorValues()
        {
            var blocks = new Dictionary<uint, List<SpirvInstruction>>();
            var loops = new List<(uint Header, uint Merge)>(); var positions = new Dictionary<uint, uint>(); var entries = new List<uint>();
            uint block = 0, function = 0; int order = 0;
            foreach (var i in binary.Instructions)
            {
                var a = i.Operands; Op op = (Op)i.Opcode;
                descriptorInstructionOrder[i] = order++;
                if (op == Op.Function) { block = 0; function = a[1]; }
                if (op == Op.Label)
                {
                    if (block == 0) entries.Add(a[0]);
                    block = a[0]; blocks[block] = []; descriptorBlockOwners[block] = function;
                }
                if (block == 0) continue;
                descriptorInstructionBlocks[i] = block;
                blocks[block].Add(i);
                if (op == Op.LoopMerge) loops.Add((block, a[0]));
                if (a.Length >= 2 && definitions.TryGetValue(a[1], out var d) && ReferenceEquals(d, i)) positions[a[1]] = block;
                if (op == Op.FunctionEnd) block = 0;
            }
            IEnumerable<uint> Successors(uint label)
            {
                var i = blocks[label].LastOrDefault(i => (Op)i.Opcode is Op.Branch or Op.BranchConditional or Op.Switch);
                if (i is null) yield break;
                var a = i.Operands;
                switch ((Op)i.Opcode)
                {
                    case Op.Branch: yield return a[0]; break;
                    case Op.BranchConditional: yield return a[1]; yield return a[2]; break;
                    case Op.Switch:
                        yield return a[1];
                        int words = types[definitions[a[0]].Operands[0]].Operands[1] == 64 ? 2 : 1;
                        for (int p = 2 + words; p < a.Length; p += words + 1) yield return a[p];
                        break;
                }
            }
            var repeatedBlocks = new HashSet<uint>();
            foreach (uint entry in entries)
            {
                var reachable = new HashSet<uint>(); var pending = new Stack<uint>(); pending.Push(entry);
                while (pending.TryPop(out uint label))
                {
                    if (!reachable.Add(label)) continue;
                    foreach (uint target in Successors(label)) if (blocks.ContainsKey(target)) pending.Push(target);
                }
                var predecessors = reachable.ToDictionary(label => label, _ => new List<uint>());
                foreach (uint label in reachable) foreach (uint target in Successors(label))
                    if (predecessors.TryGetValue(target, out var incoming)) incoming.Add(label);
                foreach (uint label in reachable) descriptorDominators[label] = label == entry ? [entry] : new(reachable);
                bool changed;
                do
                {
                    changed = false;
                    foreach (uint label in reachable.Where(label => label != entry))
                    {
                        var incoming = predecessors[label]; var dominance = new HashSet<uint>(descriptorDominators[incoming[0]]);
                        foreach (uint parent in incoming.Skip(1)) dominance.IntersectWith(descriptorDominators[parent]);
                        dominance.Add(label);
                        if (!descriptorDominators[label].SetEquals(dominance)) { descriptorDominators[label] = dominance; changed = true; }
                    }
                } while (changed);
            }
            foreach (var loop in loops)
            {
                var visited = new HashSet<uint>(); var pending = new Stack<uint>(); pending.Push(loop.Header);
                while (pending.TryPop(out uint label))
                {
                    if (label == loop.Merge || !visited.Add(label) || !blocks.ContainsKey(label)) continue;
                    repeatedBlocks.Add(label); foreach (uint target in Successors(label)) pending.Push(target);
                }
            }
            foreach (var (id, label) in positions) if (repeatedBlocks.Contains(label)) repeatedValues.Add(id);
        }
        private bool PrivateDescriptorWasWritten(SpirvInstruction load)
        {
            var slot = SlotFor(load.Operands[2], load);
            if (slot.Space == 7) return true;
            if (!descriptorInstructionBlocks.TryGetValue(load, out uint block) || !descriptorDominators.TryGetValue(block, out var dominators)) return false;
            foreach (var store in binary.Instructions.Where(i => (Op)i.Opcode == Op.Store && i.Operands.Length >= 2))
                if (ActiveSlot(store.Operands[0], out var target) && ReferenceEquals(target, slot)
                    && descriptorInstructionBlocks.TryGetValue(store, out uint source) && descriptorBlockOwners[source] == descriptorBlockOwners[block]
                    && dominators.Contains(source) && (source != block || descriptorInstructionOrder[store] < descriptorInstructionOrder[load])) return true;
            return false;
        }
        private DescriptorValue CapturedDescriptor(Node node, Shape shape) => new("capture:" + node.Instruction.Operands[1] + ":" + shape.Key,
            definitionOwners[node.Instruction.Operands[1]], !repeatedValues.Contains(node.Instruction.Operands[1]));
        private HashSet<DescriptorValue> DescriptorDomain(Node node, Shape shape) => descriptorDomains[(node.Instruction.Operands[1], shape.Key)];
        private void PrepareDescriptorIdentities()
        {
            FindRepeatedDescriptorValues();
            foreach (var node in nodes.Values) foreach (var shape in node.Shapes.Values)
                if (shape.Parts.Length != 0) descriptorDomains.Add((node.Instruction.Operands[1], shape.Key), []);
            bool changed;
            do
            {
                changed = false;
                foreach (var node in nodes.Values) foreach (var shape in node.Shapes.Values)
                {
                    if (shape.Parts.Length == 0) continue;
                    var i = node.Instruction; var a = i.Operands; var op = (Op)i.Opcode;
                    var incoming = new HashSet<DescriptorValue>();
                    if (shape.Parts[0].Constant != 0) incoming.Add(ScalarDescriptor(shape.Parts[0].Constant));
                    else if (op is Op.AccessChain or Op.InBoundsAccessChain or Op.PtrAccessChain)
                    {
                        var parent = nodes[a[2]];
                        foreach (var source in parent.Shapes.Values)
                        {
                            var target = op == Op.PtrAccessChain ? ArithmeticShape(source, i) : ShapeFor(source.Root, [.. source.Parts, .. a[3..].Select(IndexPart)]);
                            if (target.Key != shape.Key) continue;
                            if (source.Parts.Length == 0) incoming.Add(ScalarDescriptor(a[op == Op.PtrAccessChain ? 4 : 3]));
                            else if (op == Op.PtrAccessChain && source.Parts.Length == 1 && ArrayElement(source, i)) incoming.Add(CapturedDescriptor(node, shape));
                            else incoming.UnionWith(DescriptorDomain(parent, source));
                        }
                    }
                    else foreach (uint source in NodeSources(i))
                        if (nodes[source].Shapes.TryGetValue(shape.Key, out var parent)) incoming.UnionWith(DescriptorDomain(nodes[source], parent));
                    foreach (var value in incoming)
                    {
                        bool stable = op switch
                        {
                            Op.Phi or Op.Function or Op.FunctionParameter => value.Invariant,
                            Op.Load => value.Owner == 0 || value.Invariant && WithinDescriptorScope(definitionOwners[a[1]], value.Owner) && PrivateDescriptorWasWritten(i),
                            Op.FunctionCall => value.Owner == 0 || value.Invariant && WithinDescriptorScope(definitionOwners[a[1]], value.Owner),
                            _ => true
                        };
                        changed |= DescriptorDomain(node, shape).Add(stable ? value : CapturedDescriptor(node, shape));
                    }
                }
            } while (changed);
            foreach (var node in nodes.Values) foreach (var shape in node.Shapes.Values)
                if (shape.Parts.Length != 0 && shape.Parts[0].Constant == 0)
                    descriptorSnapshots[Slot(node, shape, 0)] = DescriptorIdentity(node, shape);
        }
        private string DescriptorIdentity(Node node, Shape shape)
        {
            var domain = DescriptorDomain(node, shape);
            return domain.Count == 1 ? domain.Single().Key : CapturedDescriptor(node, shape).Key;
        }
    }
}

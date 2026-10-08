using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class PointerPhiLowering
    {
        private readonly Dictionary<uint, List<SpirvInstruction>> parameters = [];
        private readonly Dictionary<uint, List<uint>> parameterSources = [];
        private readonly Dictionary<uint, uint> specializedSignatures = [];
        private readonly HashSet<uint> specializedFunctions = [];
        private void PrepareCallsAndSlots()
        {
            uint function = 0;
            foreach (var i in binary.Instructions)
            {
                if ((Op)i.Opcode == Op.Function && i.Operands.Length == 4) { function = i.Operands[1]; parameters.Add(function, []); }
                if ((Op)i.Opcode == Op.FunctionEnd) function = 0;
                if ((Op)i.Opcode == Op.ReturnValue && function != 0)
                {
                    if (i.Operands.Length != 1) throw Error("Invalid native return operands.", i);
                    returnOwners.Add(i, function);
                }
                if ((Op)i.Opcode == Op.FunctionParameter && function != 0)
                {
                    if (i.Operands.Length != 2) throw Error("Invalid native function parameter operands.", i);
                    parameters[function].Add(i); parameterSources.Add(i.Operands[1], []);
                }
            }
            foreach (var pair in definitions)
            {
                uint root = SlotRoot(pair.Key, []); if (root == 0) continue;
                if (!slots.TryGetValue(root, out var slot))
                {
                    var variable = definitions[root]; slot = new(variable) { Space = types[variable.Operands[0]].Operands[1] };
                    slots.Add(root, slot);
                    if ((Op)variable.Opcode == Op.Variable && variable.Operands.Length == 4) slot.Sources.Add(variable.Operands[3]);
                }
                slotAddresses.Add(pair.Key, slot);
            }
            void Connect(PointerSlot target, PointerSlot source, bool alias, SpirvInstruction anchor)
            {
                uint targetType = types[target.Variable.Operands[0]].Operands[2], sourceType = types[source.Variable.Operands[0]].Operands[2];
                if (alias && target.Space != source.Space || !types[targetType].Operands.AsSpan(1).SequenceEqual(types[sourceType].Operands.AsSpan(1)))
                    throw Error("Pointer-slot transfer type mismatch.", anchor);
                target.Incoming.Add(source); target.Related.Add(source); source.Related.Add(target);
                if (alias) source.Incoming.Add(target);
            }
            foreach (var i in binary.Instructions)
            {
                var a = i.Operands; Op op = (Op)i.Opcode;
                if (op == Op.Store && a.Length >= 2 && slotAddresses.TryGetValue(a[0], out var target)) target.Sources.Add(a[1]);
                if (op == Op.CopyMemory && a.Length >= 2 && (slotAddresses.ContainsKey(a[0]) || slotAddresses.ContainsKey(a[1])))
                {
                    if (!slotAddresses.TryGetValue(a[0], out var destination) || !slotAddresses.TryGetValue(a[1], out var source))
                        throw Error("Pointer-slot copy requires known source and destination slots.", i);
                    Connect(destination, source, false, i);
                }
                if (op == Op.FunctionCall && a.Length >= 3 && parameters.TryGetValue(a[2], out var args))
                {
                    if (a.Length != args.Count + 3) throw Error("Invalid native function argument count.", i);
                    for (int p = 0; p < args.Count; p++)
                    {
                        uint parameter = args[p].Operands[1], actual = a[p + 3]; parameterSources[parameter].Add(actual);
                        if (slotAddresses.TryGetValue(parameter, out var formal))
                        {
                            if (!slotAddresses.TryGetValue(actual, out var value)) throw Error("Pointer-slot helper requires a known slot argument.", i);
                            Connect(formal, value, true, i);
                        }
                    }
                }
            }
        }
        private IEnumerable<uint> ParameterSources(SpirvInstruction parameter) => parameterSources.TryGetValue(parameter.Operands[1], out var sources)
            ? sources : throw Error("Pointer parameter has no native function owner.", parameter);
        private void CompleteSlotSources()
        {
            int oldNodes, oldSlots;
            do
            {
                oldNodes = nodes.Count; oldSlots = slots.Values.Count(s => s.Active);
                foreach (var slot in slots.Values.Where(s => s.Active).ToArray()) foreach (uint source in slot.Sources) NodeFor(source);
            } while (oldNodes != nodes.Count || oldSlots != slots.Values.Count(s => s.Active));
        }
        private IEnumerable<uint> ParameterTypes(SpirvInstruction parameter)
        {
            var a = parameter.Operands;
            if (ActiveSlot(a[1], out var slot))
            {
                yield return FindType(Op.TypePointer, [slot.Space, uintType]);
                foreach (var shape in slot.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        yield return FindType(Op.TypePointer, [slot.Space, shape.Parts[p].Type]);
            }
            else if (nodes.TryGetValue(a[1], out var node))
            {
                yield return uintType;
                foreach (var shape in node.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0) yield return shape.Parts[p].Type;
            }
            else yield return a[0];
        }
        private void PrepareSignatures()
        {
            foreach (var pair in parameters)
                if (returns.ContainsKey(pair.Key) || pair.Value.Any(p => ActiveSlot(p.Operands[1], out _) || nodes.ContainsKey(p.Operands[1])))
                {
                    var function = definitions[pair.Key];
                    uint resultType = returns.TryGetValue(pair.Key, out var result) ? result.Type : function.Operands[0];
                    specializedSignatures.Add(pair.Key, FindType(Op.TypeFunction, [resultType, .. pair.Value.SelectMany(ParameterTypes)]));
                    specializedFunctions.Add(pair.Key);
                }
        }
        private bool RewriteCallInstruction(SpirvInstruction i, List<SpirvInstruction> output, List<Node> pendingParameters)
        {
            var a = i.Operands; Op op = (Op)i.Opcode;
            if (op == Op.Function && a.Length == 4 && specializedSignatures.TryGetValue(a[1], out uint signature))
            { Add(output, i, Op.Function, returns.TryGetValue(a[1], out var result) ? result.Type : a[0], a[1], a[2], signature); return true; }
            if (op == Op.FunctionParameter && ActiveSlot(a[1], out var slot))
            {
                var ids = new List<uint> { slot.Selector };
                foreach (var shape in slot.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0) ids.Add(slot.Indices[(shape.Key, p)]);
                foreach (var pair in ParameterTypes(i).Zip(ids)) Add(output, i, Op.FunctionParameter, pair.First, pair.Second);
                return true;
            }
            if (op == Op.FunctionParameter && nodes.TryGetValue(a[1], out var node))
            {
                Add(output, i, Op.FunctionParameter, uintType, node.Selector);
                foreach (var shape in node.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Add(output, i, Op.FunctionParameter, shape.Parts[p].Type, Slot(node, shape, p));
                pendingParameters.Add(node); return true;
            }
            if (op == Op.FunctionCall && a.Length >= 3 && specializedSignatures.ContainsKey(a[2]))
            {
                var arguments = new List<uint>(); var args = parameters[a[2]];
                for (int p = 0; p < args.Count; p++)
                {
                    uint actual = a[p + 3];
                    if (ActiveSlot(args[p].Operands[1], out var formal))
                    {
                        var value = SlotFor(actual, i); arguments.Add(actual);
                        foreach (var shape in formal.Shapes.Values)
                            for (int index = 0; index < shape.Parts.Length; index++) if (shape.Parts[index].Constant == 0)
                                arguments.Add(value.Indices[(shape.Key, index)]);
                    }
                    else if (nodes.TryGetValue(args[p].Operands[1], out var parameter))
                    {
                        var value = nodes[actual]; arguments.Add(value.Selector);
                        foreach (var shape in parameter.Shapes.Values)
                            for (int index = 0; index < shape.Parts.Length; index++) if (shape.Parts[index].Constant == 0)
                                arguments.Add(Slot(value, shape, index));
                    }
                    else arguments.Add(actual);
                }
                if (returns.TryGetValue(a[2], out var result))
                {
                    uint value = Id(); var returned = nodes[a[1]];
                    Add(output, i, Op.FunctionCall, [result.Type, value, a[2], .. arguments]);
                    Add(output, i, Op.CompositeExtract, uintType, returned.Selector, value, 0);
                    uint field = 1;
                    foreach (var (shape, position) in ReturnFields(result.Node))
                        Add(output, i, Op.CompositeExtract, shape.Parts[position].Type, Slot(returned, shape, position), value, field++);
                    Materialize(returned, output);
                }
                else Add(output, i, Op.FunctionCall, [a[0], a[1], a[2], .. arguments]);
                return true;
            }
            return false;
        }
    }
}

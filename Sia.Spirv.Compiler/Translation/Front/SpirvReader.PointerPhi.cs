using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    // Finite logical addresses are defunctionalized into scalar SSA state.
    // Scalar phis retain simultaneous edge semantics, including loop backedges.
    private sealed partial class PointerPhiLowering(SpirvBinary binary)
    {
        private readonly Dictionary<uint, SpirvInstruction> types = [];
        private readonly Dictionary<uint, SpirvInstruction> definitions = [];
        private readonly HashSet<uint> constants = [];
        private readonly Dictionary<uint, Node> nodes = [];
        private readonly Dictionary<string, Shape> shapes = new(StringComparer.Ordinal);
        private readonly List<SpirvInstruction> addedTypes = [], addedConstants = [];
        private readonly Dictionary<(uint Type, uint Value), uint> literals = [];
        private uint next = binary.Bound, uintType, boolType;
        private sealed record Part(uint Constant, uint Type);
        private sealed record Shape(uint Root, Part[] Parts, uint Tag, string Key);
        private sealed class Node(SpirvInstruction instruction)
        {
            public SpirvInstruction Instruction { get; } = instruction;
            public Dictionary<string, Shape> Shapes { get; } = new(StringComparer.Ordinal);
            public Dictionary<(string Shape, int Position), uint> Indices { get; } = [];
            public uint Selector;
            public bool Arithmetic;
            public bool Root => (Op)Instruction.Opcode == Op.Variable;
            public bool Null => (Op)Instruction.Opcode == Op.ConstantNull;
        }
        private static ShaderException Error(string message, SpirvInstruction instruction) =>
            new(DiagnosticStage.SpirvParse, message, new(instruction.WordOffset * 4, instruction.WordCount * 4));
        private uint Id()
        {
            if (next >= 0x3fffff) throw Error("Pointer provenance normalization exceeds the SPIR-V ID bound.", binary.Instructions[0]);
            return next++;
        }
        public static SpirvBinary Run(SpirvBinary binary) => RunWithHelpers(binary, out _, out _);
        public static SpirvBinary RunWithHelpers(SpirvBinary binary, out IReadOnlySet<uint> helpers, out IReadOnlyDictionary<uint, string> snapshots)
        {
            var pass = new PointerPhiLowering(binary); var result = pass.Rewrite(); helpers = pass.specializedFunctions; snapshots = pass.descriptorSnapshots; return result;
        }
        private SpirvBinary Rewrite()
        {
            var pointerTypes = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands.Length == 3)
                .Select(i => i.Operands[0]).ToHashSet();
            var slotTypes = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands.Length == 3 && pointerTypes.Contains(i.Operands[2]))
                .Select(i => i.Operands[0]).ToHashSet();
            var pointerDefinitions = binary.Instructions.Where(i => i.Operands.Length >= 2 && pointerTypes.Contains(i.Operands[0]))
                .GroupBy(i => i.Operands[1]).ToDictionary(g => g.Key, g => g.First());
            bool Direct(uint id, HashSet<uint> visited)
            {
                if (!visited.Add(id) || !pointerDefinitions.TryGetValue(id, out var i)) return false;
                return (Op)i.Opcode switch
                {
                    Op.Variable or Op.ConstantNull => true,
                    Op.AccessChain or Op.InBoundsAccessChain or Op.CopyObject when i.Operands.Length >= 3 => Direct(i.Operands[2], visited),
                    Op.Select when i.Operands.Length == 5 => Direct(i.Operands[3], new(visited)) && Direct(i.Operands[4], new(visited)),
                    _ => false
                };
            }
            bool Candidate(SpirvInstruction i) => i.Operands.Length >= 2 && pointerTypes.Contains(i.Operands[0])
                && ((Op)i.Opcode is Op.Phi or Op.Load or Op.Function or Op.FunctionCall or Op.ConstantNull or Op.PtrAccessChain
                    || (Op)i.Opcode == Op.Select && i.Operands.Length == 5 && Direct(i.Operands[3], []) && Direct(i.Operands[4], []));
            bool Comparison(SpirvInstruction i) => (Op)i.Opcode is Op.PtrEqual or Op.PtrNotEqual or Op.PtrDiff;
            bool SlotCandidate(SpirvInstruction i) => (Op)i.Opcode is Op.Variable or Op.FunctionParameter && i.Operands.Length >= 2 && slotTypes.Contains(i.Operands[0]);
            if (!binary.Instructions.Any(i => Candidate(i) || SlotCandidate(i) || Comparison(i))) return binary;
            var firstPhi = binary.Instructions.First(i => Candidate(i) || SlotCandidate(i) || Comparison(i));
            if (!binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands is [4441 or 4442]))
                throw Error("Pointer Phi/load/return requires a variable-pointer capability.", firstPhi);
            foreach (var i in binary.Instructions)
                if (((Op)i.Opcode).ToString().StartsWith("Type", StringComparison.Ordinal) && i.Operands.Length > 0)
                {
                    if (i.Operands[0] == 0 || i.Operands[0] >= binary.Bound) throw Error("Native type exceeds its original header bound.", i);
                    if (!types.TryAdd(i.Operands[0], i)) throw Error("Duplicate native type ID.", i);
                }
            bool function = false; uint owner = 0;
            foreach (var i in binary.Instructions)
            {
                Op op = (Op)i.Opcode; var a = i.Operands;
                if (op == Op.Function) { function = true; owner = a.Length >= 2 ? a[1] : 0; }
                if (op == Op.Label && (a.Length != 1 || a[0] == 0 || a[0] >= binary.Bound)) throw Error("Invalid native label ID.", i);
                bool constant = op is Op.Constant or Op.ConstantTrue or Op.ConstantFalse or Op.ConstantComposite or Op.ConstantNull
                    or Op.SpecConstant or Op.SpecConstantTrue or Op.SpecConstantFalse or Op.SpecConstantComposite or Op.SpecConstantOp;
                if ((function || constant || op is Op.Variable or Op.Undef) && a.Length >= 2 && types.ContainsKey(a[0]))
                {
                    if (a[1] == 0 || a[1] >= binary.Bound) throw Error("Native value exceeds its original header bound.", i);
                    if (!definitions.TryAdd(a[1], i)) throw Error("Duplicate native value ID.", i);
                    definitionOwners.Add(a[1], owner);
                    if (constant) constants.Add(a[1]);
                    if (op == Op.Constant && a.Length == 3) literals.TryAdd((a[0], a[2]), a[1]);
                }
                if (op == Op.FunctionEnd) { function = false; owner = 0; }
            }
            PrepareCallsAndSlots();
            foreach (var i in binary.Instructions.Where(SlotCandidate)) SlotFor(i.Operands[1], i);
            foreach (var i in binary.Instructions.Where(Candidate)) NodeFor(i.Operands[1]);
            foreach (var i in binary.Instructions.Where(Comparison)) PreparePointerComparison(i);
            CompleteSlotSources();
            bool changed;
            do
            {
                changed = false;
                foreach (var node in nodes.Values)
                {
                    var i = node.Instruction; var a = i.Operands;
                    if (!node.Arithmetic && ((Op)i.Opcode == Op.PtrAccessChain || NodeSources(i).Any(id => nodes[id].Arithmetic)))
                    { node.Arithmetic = true; changed = true; }
                    IEnumerable<Shape> incoming = node.Null ? [ShapeFor(0, [])] : node.Root ? [ShapeFor(a[1], [])]
                        : NodeSources(i).SelectMany(id => nodes[id].Shapes.Values).ToArray();
                    foreach (var shape in incoming)
                    {
                        Shape result = (Op)i.Opcode == Op.PtrAccessChain ? ArithmeticShape(shape, i)
                            : (Op)i.Opcode is Op.AccessChain or Op.InBoundsAccessChain
                            ? ShapeFor(shape.Root, [.. shape.Parts, .. a[3..].Select(IndexPart)]) : shape;
                        if (result.Parts.Length > types.Count) throw Error("Recursive pointer access path is invalid.", i);
                        changed |= node.Shapes.TryAdd(result.Key, result);
                    }
                }
            } while (changed);
            uintType = FindType(Op.TypeInt, [32, 0]); boolType = FindType(Op.TypeBool, []);
            foreach (var node in nodes.Values)
            {
                if (node.Shapes.Count == 0) throw Error("Pointer has no initialized finite provenance.", node.Instruction);
                if ((Op)node.Instruction.Opcode is Op.AccessChain or Op.InBoundsAccessChain or Op.PtrAccessChain && node.Shapes.Values.Any(s => s.Root == 0))
                    throw Error("Access chains based on null pointers require separate address semantics.", node.Instruction);
                node.Selector = node.Root || node.Null ? Literal(uintType, node.Shapes.Values.Single().Tag) : Id();
                foreach (var shape in node.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++)
                        if (shape.Parts[p].Constant == 0) node.Indices.Add((shape.Key, p), Id());
            }
            AllocateSlots();
            PrepareDescriptorIdentities();
            PrepareReturns();
            PrepareSignatures();
            var output = new List<SpirvInstruction>(); var pending = new List<Node>(); var pendingParameters = new List<Node>();
            bool entryStarted = false;
            foreach (var i in binary.Instructions)
            {
                Op op = (Op)i.Opcode;
                if (entryStarted && op is not (Op.Variable or Op.Line or Op.NoLine or Op.Nop))
                {
                    foreach (var parameter in pendingParameters) Materialize(parameter, output);
                    pendingParameters.Clear(); entryStarted = false;
                }
                if (op is not (Op.Phi or Op.Line or Op.NoLine or Op.Nop))
                {
                    foreach (var pendingNode in pending) Materialize(pendingNode, output);
                    pending.Clear();
                }
                if (op == Op.Phi && i.Operands.Length >= 2 && nodes.TryGetValue(i.Operands[1], out var phi))
                { Shadows(phi, output); pending.Add(phi); continue; }
                if (op == Op.PtrAccessChain && nodes.TryGetValue(i.Operands[1], out var arithmetic))
                { ArithmeticShadows(arithmetic, output); Materialize(arithmetic, output); continue; }
                if (RewritePointerComparison(i, output)) continue;
                if (RewriteReturnInstruction(i, output)) continue;
                if (RewriteCallInstruction(i, output, pendingParameters)) continue;
                if (RewriteSlotInstruction(i, output)) continue;
                output.Add(i);
                if (op == Op.Label && pendingParameters.Count != 0) entryStarted = true;
                if (op != Op.Function && i.Operands.Length >= 2 && nodes.TryGetValue(i.Operands[1], out var node) && ReferenceEquals(i, node.Instruction) && !node.Root && !node.Null)
                    Shadows(node, output);
            }
            int firstFunction = output.FindIndex(i => (Op)i.Opcode == Op.Function);
            output.InsertRange(firstFunction, addedTypes.Concat(addedConstants).Concat(addedSlotGlobals));
            if (addedPointerDecorations.Count != 0)
                output.InsertRange(output.FindIndex(i => types.ContainsKey(i.Operands.FirstOrDefault()) && ((Op)i.Opcode).ToString().StartsWith("Type", StringComparison.Ordinal)), addedPointerDecorations);
            UpdateSlotInterfaces(output);
            return new() { Version = binary.Version, Generator = binary.Generator, Bound = next, Instructions = output };
        }
        private bool IsPointer(uint type) => types.TryGetValue(type, out var i) && (Op)i.Opcode == Op.TypePointer && i.Operands.Length == 3;
        private Node NodeFor(uint id)
        {
            if (nodes.TryGetValue(id, out var existing)) return existing;
            if (!definitions.TryGetValue(id, out var i) || !IsPointer(i.Operands[0]))
                throw Error("Pointer provenance references an undefined pointer.", binary.Instructions.FirstOrDefault(x => (Op)x.Opcode is Op.Phi or Op.Load) ?? binary.Instructions[0]);
            var a = i.Operands; Op op = (Op)i.Opcode;
            if (op is not (Op.Variable or Op.ConstantNull or Op.Function or Op.FunctionCall or Op.FunctionParameter or Op.AccessChain or Op.InBoundsAccessChain or Op.PtrAccessChain or Op.CopyObject or Op.Select or Op.Phi or Op.Load))
                throw Error("Pointer source requires unsupported provenance lowering.", i);
            if (op == Op.Phi && (a.Length < 4 || a.Length % 2 != 0) || op == Op.Select && a.Length != 5
                || op == Op.CopyObject && a.Length != 3 || op is Op.AccessChain or Op.InBoundsAccessChain or Op.Load or Op.FunctionCall && a.Length < 3
                || op == Op.PtrAccessChain && a.Length < 4 || op == Op.Function && a.Length != 4 || op == Op.ConstantNull && a.Length != 2)
                throw Error("Invalid pointer provenance operands.", i);
            var result = new Node(i); nodes.Add(id, result);
            foreach (uint source in NodeSources(i))
            {
                var parent = NodeFor(source);
                if (op is Op.Phi or Op.Select or Op.CopyObject or Op.Load or Op.FunctionParameter or Op.Function or Op.FunctionCall && !types[a[0]].Operands.AsSpan(1).SequenceEqual(types[parent.Instruction.Operands[0]].Operands.AsSpan(1)))
                    throw Error("Pointer provenance source type mismatch.", i);
            }
            if (op is Op.Phi or Op.Load or Op.FunctionCall or Op.ConstantNull or Op.PtrAccessChain)
            {
                uint space = types[a[0]].Operands[1];
                if (space is not (4 or 12)) throw Error("Pointer Phi/load requires storage or workgroup memory.", i);
                if (space == 4 && !binary.Instructions.Any(x => (Op)x.Opcode == Op.Capability && x.Operands is [4442]))
                    throw Error("Workgroup pointer Phi/load requires the full VariablePointers capability.", i);
                bool Matrix(uint type, HashSet<uint> visited)
                {
                    if (!visited.Add(type) || !types.TryGetValue(type, out var t)) return false;
                    return (Op)t.Opcode switch
                    {
                        Op.TypeMatrix => true, Op.TypeArray or Op.TypeRuntimeArray => Matrix(t.Operands[1], visited),
                        Op.TypeStruct => t.Operands[1..].Any(member => Matrix(member, visited)), _ => false
                    };
                }
                if (Matrix(types[a[0]].Operands[2], [])) throw Error("Variable pointers cannot point to objects containing matrices.", i);
            }
            if (op is Op.AccessChain or Op.InBoundsAccessChain) ValidateAccess(i);
            if (op == Op.PtrAccessChain) ValidateArithmetic(i);
            return result;
        }
        private static IEnumerable<uint> Sources(SpirvInstruction i)
        {
            var a = i.Operands;
            switch ((Op)i.Opcode)
            {
                case Op.AccessChain: case Op.InBoundsAccessChain: case Op.PtrAccessChain: case Op.CopyObject: yield return a[2]; break;
                case Op.Select: yield return a[3]; yield return a[4]; break;
                case Op.Phi: for (int p = 2; p < a.Length; p += 2) yield return a[p]; break;
            }
        }
        private Part IndexPart(uint id)
        {
            if (!definitions.TryGetValue(id, out var i) || !types.TryGetValue(i.Operands[0], out var type) || (Op)type.Opcode != Op.TypeInt)
                throw Error("Pointer provenance index must have an integer type.", i ?? binary.Instructions[0]);
            if (constants.Contains(id)) return new(id, 0);
            return new(0, i.Operands[0]);
        }
        private void ValidateAccess(SpirvInstruction i)
        {
            var a = i.Operands; uint type = types[nodes[a[2]].Instruction.Operands[0]].Operands[2];
            foreach (uint index in a[3..])
            {
                if (!types.TryGetValue(type, out var t)) throw Error("Undefined pointer access type.", i);
                if ((Op)t.Opcode == Op.TypeStruct)
                {
                    if (!definitions.TryGetValue(index, out var c) || (Op)c.Opcode != Op.Constant || c.Operands.Length != 3
                        || c.Operands[2] >= t.Operands.Length - 1) throw Error("Invalid pointer structure index.", i);
                    type = t.Operands[1 + (int)c.Operands[2]];
                }
                else if ((Op)t.Opcode is Op.TypeArray or Op.TypeRuntimeArray or Op.TypeVector or Op.TypeMatrix) type = t.Operands[1];
                else throw Error("Invalid pointer provenance access path.", i);
            }
            if (type != types[a[0]].Operands[2]) throw Error("Pointer access result type mismatch.", i);
        }
        private Shape ShapeFor(uint root, Part[] parts)
        {
            string key = root + ":" + string.Join('/', parts.Select(p => p.Constant != 0 ? "c" + p.Constant : "d" + p.Type));
            if (!shapes.TryGetValue(key, out var shape)) shapes.Add(key, shape = new(root, parts, root == 0 ? 0 : (uint)shapes.Count + 1, key));
            return shape;
        }
        private uint FindType(Op op, uint[] arguments)
        {
            var existing = types.Values.FirstOrDefault(i => (Op)i.Opcode == op && i.Operands.AsSpan(1).SequenceEqual(arguments));
            if (existing is not null) return existing.Operands[0];
            uint id = Id(); var added = new SpirvInstruction((ushort)op, [id, .. arguments]);
            types.Add(id, added); addedTypes.Add(added); return id;
        }
        private uint Literal(uint type, uint value)
        {
            if (literals.TryGetValue((type, value), out uint existing)) return existing;
            uint id = Id(); uint[] words = types[type].Operands[1] == 64 ? [type, id, value, 0] : [type, id, value];
            addedConstants.Add(new((ushort)Op.Constant, words)); literals.Add((type, value), id); return id;
        }
        private void Add(List<SpirvInstruction> output, SpirvInstruction anchor, Op op, params uint[] operands) =>
            output.Add(new((ushort)op, operands, anchor.WordOffset));
        private uint Slot(Node node, Shape shape, int p) => node.Indices.TryGetValue((shape.Key, p), out uint id) ? id : Literal(shape.Parts[p].Type, 0);
        private void Shadows(Node node, List<SpirvInstruction> output)
        {
            var i = node.Instruction; var a = i.Operands; Op op = (Op)i.Opcode;
            if (op == Op.Phi)
            {
                uint[] Incoming(Func<Node, uint> value) => a[2..].Select((id, p) => p % 2 == 0 ? value(nodes[id]) : id).ToArray();
                Add(output, i, Op.Phi, [uintType, node.Selector, .. Incoming(n => n.Selector)]);
                foreach (var shape in node.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Add(output, i, Op.Phi, [shape.Parts[p].Type, Slot(node, shape, p), .. Incoming(n => Slot(n, shape, p))]);
                return;
            }
            if (op == Op.Select)
            {
                var left = nodes[a[3]]; var right = nodes[a[4]];
                Add(output, i, Op.Select, uintType, node.Selector, a[2], left.Selector, right.Selector);
                foreach (var shape in node.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Add(output, i, Op.Select, shape.Parts[p].Type, Slot(node, shape, p), a[2], Slot(left, shape, p), Slot(right, shape, p));
                return;
            }
            var source = nodes[a[2]];
            if (op == Op.CopyObject)
            {
                Add(output, i, Op.CopyObject, uintType, node.Selector, source.Selector);
                foreach (var shape in node.Shapes.Values)
                    for (int p = 0; p < shape.Parts.Length; p++) if (shape.Parts[p].Constant == 0)
                        Add(output, i, Op.CopyObject, shape.Parts[p].Type, Slot(node, shape, p), Slot(source, shape, p));
                return;
            }
            var pairs = source.Shapes.Values.Select(s => (Source: s, Target: ShapeFor(s.Root, [.. s.Parts, .. a[3..].Select(IndexPart)]))).ToArray();
            uint selected = Literal(uintType, pairs[0].Target.Tag);
            for (int p = 1; p < pairs.Length; p++)
            {
                uint condition = Id(), result = Id();
                Add(output, i, Op.IEqual, boolType, condition, source.Selector, Literal(uintType, pairs[p].Source.Tag));
                Add(output, i, Op.Select, uintType, result, condition, Literal(uintType, pairs[p].Target.Tag), selected); selected = result;
            }
            Add(output, i, Op.CopyObject, uintType, node.Selector, selected);
            foreach (var (parent, target) in pairs)
                for (int p = 0; p < target.Parts.Length; p++) if (target.Parts[p].Constant == 0)
                    Add(output, i, Op.CopyObject, target.Parts[p].Type, Slot(node, target, p), p < parent.Parts.Length ? Slot(source, parent, p) : a[3 + p - parent.Parts.Length]);
        }
        private void Materialize(Node node, List<SpirvInstruction> output)
        {
            var i = node.Instruction; var a = i.Operands; uint selected = 0; int p = 0;
            foreach (var shape in node.Shapes.Values)
            {
                uint pointer;
                if (shape.Root == 0) pointer = NullConstant(a[0]);
                else
                {
                    pointer = Id();
                    if (!node.Arithmetic || !MaterializeArithmetic(node, shape, pointer, output))
                        Add(output, i, Op.AccessChain, [a[0], pointer, shape.Root, .. shape.Parts.Select((part, index) => part.Constant != 0 ? part.Constant : Slot(node, shape, index))]);
                }
                if (p++ == 0) { selected = pointer; continue; }
                uint condition = Id(), result = p == node.Shapes.Count ? a[1] : Id();
                Add(output, i, Op.IEqual, boolType, condition, node.Selector, Literal(uintType, shape.Tag));
                Add(output, i, Op.Select, a[0], result, condition, pointer, selected); selected = result;
            }
            if (node.Shapes.Count == 1) Add(output, i, Op.CopyObject, a[0], a[1], selected);
        }
    }
}

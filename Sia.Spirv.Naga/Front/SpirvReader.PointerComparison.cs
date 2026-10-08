using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Front;

public static partial class SpirvReader
{
    private sealed partial class PointerPhiLowering
    {
        private readonly Dictionary<bool, uint> booleanConstants = [];
        private uint Boolean(bool value)
        {
            if (booleanConstants.TryGetValue(value, out uint existing)) return existing;
            Op op = value ? Op.ConstantTrue : Op.ConstantFalse;
            var original = definitions.Values.FirstOrDefault(i => (Op)i.Opcode == op && i.Operands[0] == boolType);
            uint result = original?.Operands[1] ?? Id();
            if (original is null) addedConstants.Add(new((ushort)op, [boolType, result]));
            booleanConstants.Add(value, result); return result;
        }
        private uint CopySource(uint id)
        {
            var visited = new HashSet<uint>();
            while (visited.Add(id) && definitions.TryGetValue(id, out var i) && (Op)i.Opcode == Op.CopyObject && i.Operands.Length == 3)
                id = i.Operands[2];
            return id;
        }
        private uint PointerType(uint id, SpirvInstruction anchor) => definitions.TryGetValue(id, out var i) && IsPointer(i.Operands[0])
            ? i.Operands[0] : throw Error("Pointer comparison references an undefined pointer.", anchor);
        private void PreparePointerComparison(SpirvInstruction instruction)
        {
            var a = instruction.Operands; bool difference = (Op)instruction.Opcode == Op.PtrDiff;
            if (a.Length != 4) throw Error("Invalid pointer comparison operands.", instruction);
            if (binary.Version < 0x10400) throw Error("Pointer comparisons require SPIR-V 1.4 or later.", instruction);
            if (!types.TryGetValue(a[0], out var result) || (Op)result.Opcode != (difference ? Op.TypeInt : Op.TypeBool))
                throw Error(difference ? "Pointer difference result must be an integer scalar." : "Pointer comparison result must be a Boolean scalar.", instruction);
            uint leftType = PointerType(a[2], instruction), rightType = PointerType(a[3], instruction);
            var type = types[leftType];
            if (leftType != rightType)
                throw Error("Pointer comparison operand type mismatch.", instruction);
            if (type.Operands[1] is not (4 or 12) || type.Operands[1] == 4
                && !binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands is [4442]))
                throw Error("Pointer comparisons require storage memory or full VariablePointers Workgroup memory.", instruction);
            if (difference && type.Operands[1] == 12 && !binary.Instructions.Any(i => (Op)i.Opcode == Op.Decorate
                && i.Operands is [var target, 6, > 0] && target == leftType))
                throw Error("Storage pointer difference requires an ArrayStride on its pointer type.", instruction);
            if (CopySource(a[2]) == CopySource(a[3])) return;
            NodeFor(a[2]); NodeFor(a[3]);
        }
        private uint CompareValue(List<SpirvInstruction> output, SpirvInstruction anchor, Op op, uint type, params uint[] values)
        {
            uint result = Id(); Add(output, anchor, op, [type, result, .. values]); return result;
        }
        private uint IndexValue(Node node, Shape shape, int position) => shape.Parts[position].Constant is > 0 and var constant
            ? constant : Slot(node, shape, position);
        private uint IndexType(Shape shape, int position) => shape.Parts[position].Constant is > 0 and var constant
            ? definitions[constant].Operands[0] : shape.Parts[position].Type;
        private uint IndexAs(Node node, Shape shape, int position, uint type, List<SpirvInstruction> output, SpirvInstruction anchor)
        {
            uint value = IndexValue(node, shape, position), sourceType = IndexType(shape, position);
            if (sourceType != type && types[type].Operands[2] == 1)
            {
                uint unsigned = FindType(Op.TypeInt, [types[type].Operands[1], 0]);
                return CompareValue(output, anchor, Op.Bitcast, type, IndexAs(node, shape, position, unsigned, output, anchor));
            }
            return sourceType == type ? value : CompareValue(output, anchor,
                types[sourceType].Operands[1] == types[type].Operands[1] ? Op.Bitcast : Op.UConvert, type, value);
        }
        private uint EqualIndex(Node left, Shape a, Node right, Shape b, int position, List<SpirvInstruction> output, SpirvInstruction anchor)
        {
            uint width = Math.Max(32, Math.Max(types[IndexType(a, position)].Operands[1], types[IndexType(b, position)].Operands[1]));
            uint type = FindType(Op.TypeInt, [width, 0]);
            return CompareValue(output, anchor, Op.IEqual, boolType, IndexAs(left, a, position, type, output, anchor), IndexAs(right, b, position, type, output, anchor));
        }
        private uint PairCondition(Node left, Shape a, Node right, Shape b, List<SpirvInstruction> output, SpirvInstruction anchor)
        {
            uint first = CompareValue(output, anchor, Op.IEqual, boolType, left.Selector, Literal(uintType, a.Tag));
            uint second = CompareValue(output, anchor, Op.IEqual, boolType, right.Selector, Literal(uintType, b.Tag));
            return CompareValue(output, anchor, Op.LogicalAnd, boolType, first, second);
        }
        private uint PathCondition(Node left, Shape a, Node right, Shape b, int count, List<SpirvInstruction> output, SpirvInstruction anchor, out uint parent)
        {
            parent = types[definitions[a.Root].Operands[0]].Operands[2]; uint condition = Boolean(true);
            for (int position = 0; position < count; position++)
            {
                var type = types[parent];
                if ((Op)type.Opcode == Op.TypeStruct)
                {
                    uint x = definitions[a.Parts[position].Constant].Operands[2], y = definitions[b.Parts[position].Constant].Operands[2];
                    if (x != y) return Boolean(false);
                    parent = type.Operands[1 + (int)x];
                }
                else
                {
                    condition = CompareValue(output, anchor, Op.LogicalAnd, boolType, condition, EqualIndex(left, a, right, b, position, output, anchor));
                    parent = type.Operands[1];
                }
            }
            return condition;
        }
        private void RequireSameDescriptor(Node left, Shape a, Node right, Shape b, SpirvInstruction anchor)
        {
            uint rootType = types[definitions[a.Root].Operands[0]].Operands[2];
            var array = types[rootType];
            if ((Op)array.Opcode is not (Op.TypeArray or Op.TypeRuntimeArray)
                || !binary.Instructions.Any(i => (Op)i.Opcode == Op.Decorate && i.Operands is [var target, 2 or 3] && target == array.Operands[1])) return;
            if (a.Parts.Length == 0 || b.Parts.Length == 0) return;
            if (DescriptorIdentity(left, a) == DescriptorIdentity(right, b)) return;
            var x = a.Parts[0]; var y = b.Parts[0];
            if (x.Constant != 0 && y.Constant != 0)
            {
                if (x.Constant == y.Constant) return;
                var first = definitions[x.Constant]; var second = definitions[y.Constant];
                if ((Op)first.Opcode == Op.Constant && (Op)second.Opcode == Op.Constant
                    && first.Operands[2] == second.Operands[2]
                    && (first.Operands.Length == 4 ? first.Operands[3] : 0) == (second.Operands.Length == 4 ? second.Operands[3] : 0)) return;
            }
            else if (x.Constant == 0 && y.Constant == 0 && Slot(left, a, 0) == Slot(right, b, 0)) return;
            throw Error("Descriptor-array pointer comparison requires address equivalence for potentially aliased descriptor elements.", anchor);
        }
        private bool RewritePointerComparison(SpirvInstruction instruction, List<SpirvInstruction> output)
        {
            Op op = (Op)instruction.Opcode;
            if (op is not (Op.PtrEqual or Op.PtrNotEqual or Op.PtrDiff)) return false;
            var a = instruction.Operands; bool difference = op == Op.PtrDiff;
            if (CopySource(a[2]) == CopySource(a[3]))
            {
                Add(output, instruction, Op.CopyObject, a[0], a[1], difference ? Literal(a[0], 0) : Boolean(op == Op.PtrEqual)); return true;
            }
            var left = nodes[a[2]]; var right = nodes[a[3]];
            if (!difference && (left.Shapes.Values.All(s => s.Root == 0) || right.Shapes.Values.All(s => s.Root == 0)))
            {
                var value = left.Shapes.Values.All(s => s.Root == 0) ? right : left;
                Add(output, instruction, op == Op.PtrEqual ? Op.IEqual : Op.INotEqual, a[0], a[1], value.Selector, Literal(uintType, 0)); return true;
            }
            uint result = difference ? Literal(a[0], 0) : Boolean(false);
            foreach (var x in left.Shapes.Values) foreach (var y in right.Shapes.Values)
            {
                if (x.Root != y.Root)
                {
                    if (x.Root != 0 && y.Root != 0 && types[PointerType(a[2], instruction)].Operands[1] == 12)
                        throw Error("Cross-buffer pointer comparison requires address equivalence for potentially aliased bindings.", instruction);
                    continue;
                }
                if (x.Root != 0 && x.Parts.Length != y.Parts.Length) continue;
                if (x.Root != 0) RequireSameDescriptor(left, x, right, y, instruction);
                uint condition = PairCondition(left, x, right, y, output, instruction);
                if (difference)
                {
                    if (x.Root == 0 || x.Parts.Length == 0) continue;
                    uint path = PathCondition(left, x, right, y, x.Parts.Length - 1, output, instruction, out uint parent);
                    if ((Op)types[parent].Opcode is not (Op.TypeArray or Op.TypeRuntimeArray)) continue;
                    condition = CompareValue(output, instruction, Op.LogicalAnd, boolType, condition, path);
                    uint value = CompareValue(output, instruction, Op.ISub, a[0], IndexAs(left, x, x.Parts.Length - 1, a[0], output, instruction),
                        IndexAs(right, y, y.Parts.Length - 1, a[0], output, instruction));
                    result = CompareValue(output, instruction, Op.Select, a[0], condition, value, result);
                }
                else
                {
                    if (x.Root != 0) condition = CompareValue(output, instruction, Op.LogicalAnd, boolType, condition,
                        PathCondition(left, x, right, y, x.Parts.Length, output, instruction, out _));
                    result = CompareValue(output, instruction, Op.LogicalOr, boolType, result, condition);
                }
            }
            Add(output, instruction, op == Op.PtrNotEqual ? Op.LogicalNot : Op.CopyObject, a[0], a[1], result); return true;
        }
    }
}

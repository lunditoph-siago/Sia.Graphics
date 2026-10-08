using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class PointerPhiLowering
    {
        private readonly List<SpirvInstruction> addedPointerDecorations = [];
        private bool ZeroElement(uint id) => definitions.TryGetValue(id, out var value)
            && (Op)value.Opcode == Op.Constant && value.Operands.Skip(2).All(word => word == 0);
        private uint PathParent(Shape shape, SpirvInstruction anchor)
        {
            if (shape.Root == 0) throw Error("Pointer arithmetic based on null requires separate address semantics.", anchor);
            uint type = types[definitions[shape.Root].Operands[0]].Operands[2];
            for (int p = 0; p < shape.Parts.Length - 1; p++)
            {
                var t = types[type];
                type = (Op)t.Opcode == Op.TypeStruct
                    ? t.Operands[1 + (int)definitions[shape.Parts[p].Constant].Operands[2]] : t.Operands[1];
            }
            return type;
        }
        private bool ArrayElement(Shape shape, SpirvInstruction anchor) => shape.Parts.Length != 0
            && (Op)types[PathParent(shape, anchor)].Opcode is Op.TypeArray or Op.TypeRuntimeArray;
        private void ValidateArithmetic(SpirvInstruction instruction)
        {
            var a = instruction.Operands;
            IndexPart(a[3]);
            uint baseType = nodes[a[2]].Instruction.Operands[0];
            if (types[baseType].Operands[1] != types[a[0]].Operands[1]) throw Error("Pointer arithmetic storage class mismatch.", instruction);
            if (types[baseType].Operands[1] == 12 && !binary.Instructions.Any(i => (Op)i.Opcode == Op.Decorate
                && i.Operands is [var target, 6, > 0] && target == baseType))
                throw Error("Storage pointer arithmetic requires an ArrayStride on its base pointer type.", instruction);
            ValidateAccess(instruction with { Operands = [a[0], a[1], a[2], .. a[4..]] });
        }
        private Shape ArithmeticShape(Shape source, SpirvInstruction instruction)
        {
            var a = instruction.Operands;
            if (!ArrayElement(source, instruction))
            {
                if (!ZeroElement(a[3])) throw Error("Pointer arithmetic requires a finite innermost-array element provenance.", instruction);
                return ShapeFor(source.Root, [.. source.Parts, .. a[4..].Select(IndexPart)]);
            }
            uint width = Math.Max(32, Math.Max(types[IndexType(source, source.Parts.Length - 1)].Operands[1], types[definitions[a[3]].Operands[0]].Operands[1]));
            uint type = FindType(Op.TypeInt, [width, 0]);
            return ShapeFor(source.Root, [.. source.Parts[..^1], new Part(0, type), .. a[4..].Select(IndexPart)]);
        }
        private uint SignedElement(uint id, uint type, SpirvInstruction anchor, List<SpirvInstruction> output)
        {
            uint source = definitions[id].Operands[0], width = types[type].Operands[1];
            if (types[source].Operands[1] != width)
            {
                uint signed = FindType(Op.TypeInt, [width, 1]);
                id = CompareValue(output, anchor, Op.SConvert, signed, id); source = signed;
            }
            return source == type ? id : CompareValue(output, anchor, Op.Bitcast, type, id);
        }
        private void ArithmeticShadows(Node node, List<SpirvInstruction> output)
        {
            var i = node.Instruction; var a = i.Operands; var source = nodes[a[2]];
            var pairs = source.Shapes.Values.Select(s => (Source: s, Target: ArithmeticShape(s, i))).ToArray();
            uint selector = Literal(uintType, pairs[0].Target.Tag);
            foreach (var pair in pairs.Skip(1))
            {
                uint condition = CompareValue(output, i, Op.IEqual, boolType, source.Selector, Literal(uintType, pair.Source.Tag));
                selector = CompareValue(output, i, Op.Select, uintType, condition, Literal(uintType, pair.Target.Tag), selector);
            }
            Add(output, i, Op.CopyObject, uintType, node.Selector, selector);
            foreach (var target in node.Shapes.Values)
                for (int p = 0; p < target.Parts.Length; p++) if (target.Parts[p].Constant == 0)
                {
                    uint type = target.Parts[p].Type, selected = Literal(type, 0);
                    foreach (var pair in pairs.Where(pair => pair.Target.Key == target.Key))
                    {
                        var parent = pair.Source; uint value;
                        if (ArrayElement(parent, i) && p == parent.Parts.Length - 1)
                            value = CompareValue(output, i, Op.IAdd, type, IndexAs(source, parent, p, type, output, i), SignedElement(a[3], type, i, output));
                        else value = p < parent.Parts.Length ? IndexValue(source, parent, p) : a[4 + p - parent.Parts.Length];
                        uint condition = CompareValue(output, i, Op.IEqual, boolType, source.Selector, Literal(uintType, parent.Tag));
                        selected = CompareValue(output, i, Op.Select, type, condition, value, selected);
                    }
                    Add(output, i, Op.CopyObject, type, Slot(node, target, p), selected);
                }
        }
        private bool MaterializeArithmetic(Node node, Shape shape, uint pointer, List<SpirvInstruction> output)
        {
            var i = node.Instruction; var a = i.Operands;
            if (!ArrayElement(shape, i)) return false;
            uint parent = PathParent(shape, i), space = types[a[0]].Operands[1];
            if (space == 12)
            {
                var stride = binary.Instructions.FirstOrDefault(x => (Op)x.Opcode == Op.Decorate && x.Operands is [var target, 6, > 0] && target == parent);
                if (stride is null) throw Error("Pointer arithmetic array requires an explicit ArrayStride.", i);
                var existing = binary.Instructions.Concat(addedPointerDecorations).FirstOrDefault(x => (Op)x.Opcode == Op.Decorate && x.Operands is [var target, 6, _] && target == a[0]);
                if (existing is null) addedPointerDecorations.Add(new((ushort)Op.Decorate, [a[0], 6, stride.Operands[2]]));
                else if (existing.Operands[2] != stride.Operands[2]) throw Error("Pointer arithmetic result stride does not match its array.", i);
            }
            int last = shape.Parts.Length - 1; uint type = IndexType(shape, last), index = IndexValue(node, shape, last);
            // Do not materialize an unselected address with a potentially invalid offset.
            uint active = CompareValue(output, i, Op.IEqual, boolType, node.Selector, Literal(uintType, shape.Tag));
            index = CompareValue(output, i, Op.Select, type, active, index, Literal(type, 0));
            uint zero = CompareValue(output, i, Op.IEqual, boolType, index, Literal(type, 0));
            uint previous = CompareValue(output, i, Op.ISub, type, index, Literal(type, 1));
            uint baseIndex = CompareValue(output, i, Op.Select, type, zero, Literal(type, 0), previous);
            uint element = CompareValue(output, i, Op.Select, type, zero, Literal(type, 0), Literal(type, 1)), address = Id();
            // Anchor at index-1, then advance by one: an address at array length
            // remains a legal one-past PtrAccessChain, never an OOB AccessChain.
            Add(output, i, Op.AccessChain, [a[0], address, shape.Root, .. shape.Parts[..^1].Select((part, p) => part.Constant != 0 ? part.Constant : Slot(node, shape, p)), baseIndex]);
            Add(output, i, Op.PtrAccessChain, a[0], pointer, address, element);
            return true;
        }
    }
}

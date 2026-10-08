using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Front;

public static partial class SpirvReader
{
    private sealed partial class PointerPhiLowering
    {
        private sealed record PointerReturn(Node Node, uint Type);
        private readonly Dictionary<uint, PointerReturn> returns = [];
        private readonly Dictionary<SpirvInstruction, uint> returnOwners = [];
        private IEnumerable<uint> ReturnSources(SpirvInstruction function) => returnOwners
            .Where(p => p.Value == function.Operands[1]).Select(p => p.Key.Operands[0]);
        private static IEnumerable<(Shape Shape, int Position)> ReturnFields(Node node)
        {
            foreach (var shape in node.Shapes.Values.OrderBy(s => s.Tag))
                for (int p = 0; p < shape.Parts.Length; p++)
                    if (shape.Parts[p].Constant == 0) yield return (shape, p);
        }
        private void PrepareReturns()
        {
            foreach (var node in nodes.Values.Where(n => (Op)n.Instruction.Opcode == Op.Function))
            {
                uint type = FindType(Op.TypeStruct, [uintType, .. ReturnFields(node).Select(p => p.Shape.Parts[p.Position].Type)]);
                returns.Add(node.Instruction.Operands[1], new(node, type));
            }
        }
        private bool RewriteReturnInstruction(SpirvInstruction instruction, List<SpirvInstruction> output)
        {
            if (!returnOwners.TryGetValue(instruction, out uint owner) || !returns.TryGetValue(owner, out var result)) return false;
            var value = nodes[instruction.Operands[0]]; uint constructed = Id();
            Add(output, instruction, Op.CompositeConstruct, [result.Type, constructed, value.Selector,
                .. ReturnFields(result.Node).Select(p => Slot(value, p.Shape, p.Position))]);
            Add(output, instruction, Op.ReturnValue, constructed); return true;
        }
    }
}

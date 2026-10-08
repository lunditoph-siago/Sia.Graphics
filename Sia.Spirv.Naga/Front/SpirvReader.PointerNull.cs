using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private static bool IsNullPointer(IR.Expression expression) => expression switch
        {
            IR.Expression.Construct { Type: IR.ShaderType.Pointer, Components.Count: 0 } => true,
            IR.Expression.Unary { Operator: "&" or "*" } u => IsNullPointer(u.Operand),
            IR.Expression.Access a => IsNullPointer(a.Base), IR.Expression.Member m => IsNullPointer(m.Base),
            IR.Expression.Select s => IsNullPointer(s.Accept) && IsNullPointer(s.Reject), _ => false
        };
    }
    private sealed partial class PointerPhiLowering
    {
        private readonly Dictionary<uint, uint> nullConstants = [];
        private uint NullConstant(uint type)
        {
            if (nullConstants.TryGetValue(type, out uint existing)) return existing;
            var original = definitions.Values.FirstOrDefault(i => (Op)i.Opcode == Op.ConstantNull && i.Operands[0] == type);
            uint result = original?.Operands[1] ?? Id();
            if (original is null) addedConstants.Add(new((ushort)Op.ConstantNull, [type, result]));
            nullConstants.Add(type, result); return result;
        }
    }
}

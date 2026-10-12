using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private Expression Subgroup(Op op, uint[] a)
        {
            Count(4);
            if (ConstantUint(a[2]) != 3) throw Error("Group operation requires subgroup execution scope.");
            ShaderType type = Type(a[0]);
            if (op is >= Op.GroupNonUniformIAdd and <= Op.GroupNonUniformLogicalXor)
            {
                Count(5, 5);
                string operation = op switch
                {
                    Op.GroupNonUniformIAdd or Op.GroupNonUniformFAdd => "Add", Op.GroupNonUniformIMul or Op.GroupNonUniformFMul => "Mul",
                    Op.GroupNonUniformSMin or Op.GroupNonUniformUMin or Op.GroupNonUniformFMin => "Min",
                    Op.GroupNonUniformSMax or Op.GroupNonUniformUMax or Op.GroupNonUniformFMax => "Max",
                    Op.GroupNonUniformBitwiseAnd or Op.GroupNonUniformLogicalAnd => "And", Op.GroupNonUniformBitwiseOr or Op.GroupNonUniformLogicalOr => "Or", _ => "Xor"
                };
                string prefix = a[3] switch { 0 => "subgroup", 1 when operation is "Add" or "Mul" => "subgroupInclusive", 2 when operation is "Add" or "Mul" => "subgroupExclusive", _ => throw Error("Unsupported subgroup scan or clustered reduction.") };
                return new Expression.Call(prefix + operation, [Value(a[4])], type, CallBinding.Builtin);
            }
            string name = op switch
            {
                Op.GroupNonUniformAll => "subgroupAll", Op.GroupNonUniformAny => "subgroupAny", Op.GroupNonUniformBallot => "subgroupBallot",
                Op.GroupNonUniformBroadcastFirst => "subgroupBroadcastFirst", Op.GroupNonUniformBroadcast => "subgroupBroadcast",
                Op.GroupNonUniformShuffle => "subgroupShuffle", Op.GroupNonUniformShuffleDown => "subgroupShuffleDown", Op.GroupNonUniformShuffleUp => "subgroupShuffleUp", Op.GroupNonUniformShuffleXor => "subgroupShuffleXor",
                Op.GroupNonUniformQuadBroadcast => "quadBroadcast", Op.GroupNonUniformQuadSwap when a.Length == 5 => ConstantUint(a[4]) switch { 0 => "quadSwapX", 1 => "quadSwapY", 2 => "quadSwapDiagonal", _ => throw Error("Invalid quad swap direction.") },
                _ => throw Error("Unsupported subgroup instruction.")
            };
            int length = SubgroupBuiltins.Indexed(name) || op == Op.GroupNonUniformQuadSwap ? 5 : 4;
            Count(length, length);
            return new Expression.Call(name, SubgroupBuiltins.Indexed(name) ? [Value(a[3]), Value(a[4])] : [Value(a[3])], type, CallBinding.Builtin);
        }
    }
}

using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;

namespace Sia.Spirv.Naga.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private sealed partial class FunctionEmitter
        {
            private uint Subgroup(Expression.Call call, uint[] args)
            {
                string name = call.Function; owner.capabilities.Add(61);
                uint scope = owner.Constant(Expression.U32(3));
                if (name == "subgroupBallot")
                { owner.capabilities.Add(64); return Result(Op.GroupNonUniformBallot, call.Type, scope, args.Length == 0 ? owner.Constant(Expression.Bool(true)) : args[0]); }
                if (name is "subgroupAll" or "subgroupAny")
                { owner.capabilities.Add(62); return Result(name == "subgroupAll" ? Op.GroupNonUniformAll : Op.GroupNonUniformAny, call.Type, scope, args[0]); }
                if (SubgroupBuiltins.Gather(name))
                {
                    if (name == "subgroupBroadcastFirst") { owner.capabilities.Add(64); return Result(Op.GroupNonUniformBroadcastFirst, call.Type, scope, args[0]); }
                    if (name.StartsWith("quad", StringComparison.Ordinal))
                    {
                        owner.capabilities.Add(68);
                        uint lane = name == "quadBroadcast" ? args[1] : owner.Constant(Expression.U32(name switch { "quadSwapX" => 0u, "quadSwapY" => 1u, _ => 2u }));
                        return Result(name == "quadBroadcast" ? Op.GroupNonUniformQuadBroadcast : Op.GroupNonUniformQuadSwap, call.Type, scope, args[0], lane);
                    }
                    Op gather = name switch
                    {
                        "subgroupBroadcast" when ConstantEvaluator.TryEvaluate(call.Arguments[1], out _) => Op.GroupNonUniformBroadcast,
                        "subgroupShuffleDown" => Op.GroupNonUniformShuffleDown, "subgroupShuffleUp" => Op.GroupNonUniformShuffleUp,
                        "subgroupShuffleXor" => Op.GroupNonUniformShuffleXor, _ => Op.GroupNonUniformShuffle
                    };
                    owner.capabilities.Add(gather == Op.GroupNonUniformBroadcast ? 64u : gather is Op.GroupNonUniformShuffleDown or Op.GroupNonUniformShuffleUp ? 66u : 65u);
                    return Result(gather, call.Type, scope, args[0], args[1]);
                }
                owner.capabilities.Add(63);
                uint operation = name.StartsWith("subgroupInclusive", StringComparison.Ordinal) ? 1u : name.StartsWith("subgroupExclusive", StringComparison.Ordinal) ? 2u : 0u;
                string reduction = name.Replace("subgroupInclusive", "", StringComparison.Ordinal).Replace("subgroupExclusive", "", StringComparison.Ordinal).Replace("subgroup", "", StringComparison.Ordinal);
                ScalarKind kind = Scalar(call.Type).Kind;
                Op opcode = (reduction, kind) switch
                {
                    ("Add", ScalarKind.Float) => Op.GroupNonUniformFAdd, ("Add", _) => Op.GroupNonUniformIAdd,
                    ("Mul", ScalarKind.Float) => Op.GroupNonUniformFMul, ("Mul", _) => Op.GroupNonUniformIMul,
                    ("Min", ScalarKind.Float) => Op.GroupNonUniformFMin, ("Min", ScalarKind.Sint) => Op.GroupNonUniformSMin, ("Min", _) => Op.GroupNonUniformUMin,
                    ("Max", ScalarKind.Float) => Op.GroupNonUniformFMax, ("Max", ScalarKind.Sint) => Op.GroupNonUniformSMax, ("Max", _) => Op.GroupNonUniformUMax,
                    ("And", ScalarKind.Bool) => Op.GroupNonUniformLogicalAnd, ("And", _) => Op.GroupNonUniformBitwiseAnd,
                    ("Or", ScalarKind.Bool) => Op.GroupNonUniformLogicalOr, ("Or", _) => Op.GroupNonUniformBitwiseOr,
                    ("Xor", ScalarKind.Bool) => Op.GroupNonUniformLogicalXor, ("Xor", _) => Op.GroupNonUniformBitwiseXor,
                    _ => throw owner.Error("Unknown subgroup reduction.")
                };
                return Result(opcode, call.Type, scope, operation, args[0]);
            }
        }
    }
}

namespace Sia.Spirv.Compiler.Translation.IR;

internal static class SubgroupBuiltins
{
    private static readonly HashSet<string> Names = new(("subgroupBallot subgroupAll subgroupAny subgroupAdd subgroupMul subgroupMin subgroupMax subgroupAnd subgroupOr subgroupXor " +
        "subgroupExclusiveAdd subgroupExclusiveMul subgroupInclusiveAdd subgroupInclusiveMul subgroupBroadcastFirst subgroupBroadcast subgroupShuffle subgroupShuffleDown subgroupShuffleUp subgroupShuffleXor " +
        "quadBroadcast quadSwapX quadSwapY quadSwapDiagonal").Split(' '), StringComparer.Ordinal);
    internal static bool Contains(string name) => Names.Contains(name);
    internal static bool Gather(string name) => name is "subgroupBroadcastFirst" or "subgroupBroadcast" or "subgroupShuffle" or "subgroupShuffleDown" or "subgroupShuffleUp" or "subgroupShuffleXor" or "quadBroadcast" or "quadSwapX" or "quadSwapY" or "quadSwapDiagonal";
    internal static bool Indexed(string name) => name is "subgroupBroadcast" or "subgroupShuffle" or "subgroupShuffleDown" or "subgroupShuffleUp" or "subgroupShuffleXor" or "quadBroadcast";
}

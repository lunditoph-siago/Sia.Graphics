using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class SubgroupTests
{
    [Theory]
    [InlineData("subgroupAdd", 0u)][InlineData("subgroupInclusiveAdd", 1u)][InlineData("subgroupExclusiveAdd", 2u)]
    public void ReductionAndScanModesSurviveBothBackends(string operation, uint mode)
    {
        string source = $"@compute @workgroup_size(1) fn main(@builtin(subgroup_invocation_id) lane: u32) {{ let value = {operation}(lane); }}";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.GroupNonUniformIAdd && i.Operands[3] == mode);
        string output = ShaderTranslator.SpirvToWgsl(binary.ToBytes()); Assert.Contains(operation + "(", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Fact]
    public void NonconstantBroadcastLaneUsesShuffleInSpirv13()
    {
        const string source = "@compute @workgroup_size(1) fn main(@builtin(subgroup_invocation_id) lane: u32) { let x = subgroupBroadcast(lane, lane); let y = subgroupBroadcast(lane, 0u); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.GroupNonUniformShuffle);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.GroupNonUniformBroadcast);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void SubgroupIoHasDistinctCorrectSpirvBuiltinNumbers()
    {
        const string source = "@compute @workgroup_size(1) fn main(@builtin(num_subgroups) count: u32, @builtin(subgroup_id) group: u32, @builtin(subgroup_size) size: u32, @builtin(subgroup_invocation_id) lane: u32) { let mask = subgroupBallot(); let vote = subgroupAll(lane == 0u); let q = quadSwapDiagonal(lane); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        uint[] builtins = binary.Instructions.Where(i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 11).Select(i => i.Operands[2]).ToArray();
        Assert.Equal(new uint[] { 38, 40, 36, 41 }, builtins);
        var swap = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.GroupNonUniformQuadSwap);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Constant && i.Operands[1] == swap.Operands[4] && i.Operands[2] == 2);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Theory]
    [InlineData("@compute @workgroup_size(1) fn main() { let x = subgroupAll(1u); }")]
    [InlineData("@compute @workgroup_size(1) fn main() { let x = subgroupAnd(1.0f); }")]
    [InlineData("@vertex fn main() -> @builtin(position) vec4f { let x = subgroupAdd(1u); return vec4f(); }")]
    public void RejectsInvalidSubgroupTypesAndStages(string source) => Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source));
}

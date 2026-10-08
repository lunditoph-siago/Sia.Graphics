using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Proc;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class WorkgroupSpecializationTests
{
    private const string Source = "@id(7) override width=3u; @id(8) override height=2i; @compute @workgroup_size(width+1u,height,2u) fn main() {}";

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void DependentSignedAndUnsignedDimensionsRemainSpecializable(bool ids)
    {
        byte[] bytes = SpirvWriter.Write(WgslReader.Parse(Source), new() { UseLocalSizeId = ids });
        var binary = SpirvBinary.Parse(bytes);
        Assert.Equal(ids, binary.Instructions.Any(i => (Op)i.Opcode == Op.ExecutionModeId && i.Operands[1] == 38));
        Assert.Equal(!ids, binary.Instructions.Any(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 11, 25]));
        var back = SpirvReader.Parse(bytes); ModuleValidator.Validate(back);
        var resolved = PipelineConstantResolver.Resolve(back, new Dictionary<string, double> { ["7"] = 6, ["8"] = 3 });
        Assert.Equal(new uint[] { 7, 3, 2 }, resolved.Functions.Single(f => f.Stage == ShaderStage.Compute).WorkgroupSize.Select(e => Convert.ToUInt32(Assert.IsType<Expression.Literal>(e).Value)));
        string wgsl = WgslWriter.Write(back); ModuleValidator.Validate(WgslReader.Parse(wgsl));
        var reemitted = SpirvWriter.Write(back, new() { UseLocalSizeId = ids }); ModuleValidator.Validate(SpirvReader.Parse(reemitted));
    }

    [Fact]
    public void DistinctEntrySizesRequireLocalSizeIdOrResolution()
    {
        var module = WgslReader.Parse("@id(7) override width=3u; @compute @workgroup_size(width) fn first() {} @compute @workgroup_size(width+1u) fn second() {}");
        Assert.Contains("UseLocalSizeId", Assert.Throws<ShaderException>(() => SpirvWriter.Write(module)).Message);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, new() { UseLocalSizeId = true }));
        Assert.Equal(2, binary.Instructions.Count(i => (Op)i.Opcode == Op.ExecutionModeId));
        var resolved = PipelineConstantResolver.Resolve(SpirvReader.Parse(binary.ToBytes()), new Dictionary<string, double> { ["7"] = 5 });
        Assert.Equal(new uint[] { 5, 6 }, resolved.Functions.Where(f => f.Stage == ShaderStage.Compute).Select(f => Convert.ToUInt32(Assert.IsType<Expression.Literal>(f.WorkgroupSize[0]).Value)));
        byte[] concrete = SpirvWriter.Write(module, new() { PipelineConstants = new Dictionary<string, double> { ["7"] = 4 } });
        Assert.DoesNotContain(SpirvBinary.Parse(concrete).Instructions, i => (Op)i.Opcode == Op.ExecutionModeId);
    }

    [Fact]
    public void EqualEntrySizesShareOneBuiltinConstant()
    {
        const string source = "@id(7) override width=3u; @compute @workgroup_size(width) fn first() {} @compute @workgroup_size(width) fn second() {}";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 11, 25]);
        var back = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(back);
        Assert.Equal(2, back.Functions.Count(f => f.Stage == ShaderStage.Compute));
    }

    [Theory]
    [InlineData("@id(7) override width=0u;", "width")]
    [InlineData("@id(7) override width=-1i;", "width")]
    [InlineData("@id(7) override width=1u;", "width-1u")]
    public void NonpositiveDefaultsRequireExplicitResolution(string declaration, string size)
    {
        var module = WgslReader.Parse(declaration+$" @compute @workgroup_size({size}) fn main() {{}}");
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module));
        var binary = SpirvWriter.Write(module, new() { PipelineConstants = new Dictionary<string, double> { ["7"] = 4 } });
        ModuleValidator.Validate(SpirvReader.Parse(binary));
    }
}

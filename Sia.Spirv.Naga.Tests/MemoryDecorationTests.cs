using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Proc;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class MemoryDecorationTests
{
    [Theory]
    [InlineData("", MemoryDecorations.None)]
    [InlineData("@coherent", MemoryDecorations.Coherent)]
    [InlineData("@volatile", MemoryDecorations.Volatile)]
    [InlineData("@coherent @volatile", MemoryDecorations.Coherent | MemoryDecorations.Volatile)]
    [InlineData("@coherent @coherent @volatile @volatile", MemoryDecorations.Coherent | MemoryDecorations.Volatile)]
    public void MemoryAttributesSurviveBothLanguagesAndPipelineResolution(string attributes, MemoryDecorations expected)
    {
        string source = $"{attributes} @group(0) @binding(0) var<storage, read_write> data: array<u32>; override value = 3u; @compute @workgroup_size(1) fn main() {{ data[0] = data[1] + value; }}";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        Assert.Equal(expected, Assert.Single(module.Globals).MemoryDecorations);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["value"] = 4 });
        Assert.Equal(expected, Assert.Single(resolved.Globals).MemoryDecorations);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(resolved));
        Assert.Equal((expected & MemoryDecorations.Coherent) != 0, binary.Instructions.Any(i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 23));
        Assert.Equal((expected & MemoryDecorations.Volatile) != 0, binary.Instructions.Any(i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 21));
        var back = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(back);
        Assert.Equal(expected, back.Globals.Single(g => g.Space == AddressSpace.Storage).MemoryDecorations);
        var wgsl = WgslReader.Parse(WgslWriter.Write(back)); ModuleValidator.Validate(wgsl);
        Assert.Equal(expected, wgsl.Globals.Single(g => g.Space == AddressSpace.Storage).MemoryDecorations);
    }

    [Theory]
    [InlineData("@coherent var<private> value: u32;")]
    [InlineData("@volatile var<workgroup> value: u32;")]
    [InlineData("@coherent @group(0) @binding(0) var<uniform> value: u32;")]
    [InlineData("@volatile @group(0) @binding(0) var image: texture_storage_2d<r32uint, read_write>;")]
    [InlineData("@coherent(1) @group(0) @binding(0) var<storage> value: u32;")]
    [InlineData("@volatile(1) @group(0) @binding(0) var<storage> value: u32;")]
    [InlineData("@coherent const value = 1;")]
    [InlineData("fn main() { @volatile var value = 1; }")]
    public void MisplacedAndArgumentBearingMemoryAttributesAreRejected(string source)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source));
    }

    [Fact]
    public void UnknownIrMemoryBitsAreRejected()
    {
        var module = new Module();
        module.Globals.Add(new("data", ShaderType.U32, AddressSpace.Storage, Binding: new(0, 0)) { MemoryDecorations = (MemoryDecorations)4 });
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }
}

using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;

namespace Sia.Spirv.Naga.Tests;

public class DirectiveTests
{
    [Theory]
    [InlineData("readonly_and_readwrite_storage_textures")]
    [InlineData("packed_4x8_integer_dot_product")]
    [InlineData("pointer_composite_access")]
    [InlineData("readonly_and_readwrite_storage_textures, packed_4x8_integer_dot_product, pointer_composite_access,")]
    public void ImplementedRequirementsAreAcceptedWithoutInventingEnableExtensions(string requirements)
    {
        var module = WgslReader.Parse($"requires {requirements}; requires pointer_composite_access; diagnostic(off, derivative_uniformity); enable f16; @compute @workgroup_size(1) fn main() {{}}");
        string output = WgslWriter.Write(module);
        Assert.DoesNotContain("requires", output);
        Assert.Equal("f16", Assert.Single(module.Enables));
        Assert.NotEmpty(SpirvWriter.Write(WgslReader.Parse(output)));
    }

    [Fact]
    public void RequiredStorageTextureFeatureWorks()
    {
        const string source = "requires readonly_and_readwrite_storage_textures; @group(0) @binding(0) var image:texture_storage_2d<r32uint,read_write>; @compute @workgroup_size(1) fn main() { let value = textureLoad(image, vec2i(0)); textureStore(image, vec2i(0), value); }";
        Assert.NotEmpty(ShaderTranslator.WgslToSpirv(source));
    }

    [Theory]
    [InlineData("requires unknown_requirement;")]
    [InlineData("requires unrestricted_pointer_parameters;")]
    [InlineData("requires ;")]
    [InlineData("requires pointer_composite_access,,;")]
    [InlineData("fn main() {} requires pointer_composite_access;")]
    [InlineData("; requires pointer_composite_access;")]
    [InlineData("enable unknown_extension;")]
    [InlineData("enable subgroups;")]
    [InlineData("enable ;")]
    [InlineData("enable f16,,;")]
    public void InvalidUnsupportedAndLateDirectivesAreRejected(string source)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source));
    }

    [Fact]
    public void IrCannotInjectDirectiveText()
    {
        var module = new Module(); module.Enables.Add("f16; fn injected(){}");
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
    }
}

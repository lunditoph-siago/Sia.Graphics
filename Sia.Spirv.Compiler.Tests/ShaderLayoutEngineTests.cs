using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Tests;

public sealed class ShaderLayoutEngineTests
{
    [Fact]
    public void LegalizeUsesAddressSpaceSpecificStructAlignment()
    {
        var type = new ShaderStructType(
            "Scalar",
            [new ShaderStructField("Value", SpirvScalarType.Float32)]);
        var engine = new ShaderLayoutEngine();

        var storage = engine.Legalize(type, ShaderAddressSpace.Storage);
        var uniform = engine.Legalize(type, ShaderAddressSpace.Uniform);

        Assert.Equal(4, storage.Alignment);
        Assert.Equal(4, storage.Size);
        Assert.DoesNotContain(storage.Members, static member => member.IsPadding);
        Assert.Equal(16, uniform.Alignment);
        Assert.Equal(16, uniform.Size);
        var padding = Assert.Single(uniform.Members, static member => member.IsPadding);
        Assert.Equal(4, padding.Offset);
        Assert.Equal(12, padding.Size);
    }

    [Fact]
    public void LegalizeCreatesStableLogicalToPhysicalMemberMapping()
    {
        var type = new ShaderStructType(
            "Aligned",
            [
                new ShaderStructField("Id", SpirvScalarType.UInt32),
                new ShaderStructField("Position", SpirvScalarType.Float32x3)
            ]);

        var layout = new ShaderLayoutEngine().Legalize(
            type,
            ShaderAddressSpace.Storage);

        Assert.Equal(0, layout.GetLogicalMember(0).PhysicalIndex);
        Assert.Equal(0, layout.GetLogicalMember(0).Offset);
        Assert.Equal(2, layout.GetLogicalMember(1).PhysicalIndex);
        Assert.Equal(16, layout.GetLogicalMember(1).Offset);
        Assert.Collection(
            layout.Members,
            member => Assert.False(member.IsPadding),
            member => {
                Assert.True(member.IsPadding);
                Assert.Equal(12, member.Size);
            },
            member => Assert.False(member.IsPadding),
            member => {
                Assert.True(member.IsPadding);
                Assert.Equal(4, member.Size);
            });
    }
}

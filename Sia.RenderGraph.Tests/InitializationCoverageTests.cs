namespace Sia.RenderGraph.Tests;

public class InitializationCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferCoverage_MergesUnorderedAdjacentWritesAndRejectsGaps(bool gap)
    {
        var builder = new RenderGraphBuilder();
        var buffer = builder.CreateBuffer(new("transient", 128));
        builder.AddComputePass("upper").Write(buffer, RenderGraphBufferUsage.Storage, new(64, 64));
        builder.AddComputePass("lower").Write(buffer, RenderGraphBufferUsage.Storage, new(0, gap ? 63UL : 64UL));
        builder.AddComputePass("read").Read(buffer, RenderGraphBufferUsage.Storage).MarkSideEffect();
        var definition = builder.Build();

        if (gap) {
            Assert.Throws<RenderGraphCompilationException>(() => RenderGraphCompiler.Compile(definition));
        }
        else {
            var compiled = RenderGraphCompiler.Compile(definition);
            Assert.Equal(3, compiled.Passes.Count);
            Assert.Equal([0, 1, 2], compiled.Passes.Select(pass => pass.DeclarationIndex));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextureCoverage_ChecksEveryMipAndLayer(bool gap)
    {
        var builder = new RenderGraphBuilder();
        var texture = builder.CreateTexture(new("array", RenderGraphTextureFormat.RGBA8Unorm,
            16, 16, 4, 4));
        for (uint mip = 0; mip < 4; mip++) {
            for (uint layer = 0; layer < 4; layer++) {
                if (gap && mip == 2 && layer == 1) { continue; }
                builder.AddComputePass($"write {mip}/{layer}")
                    .Write(texture, RenderGraphTextureUsage.CopyDestination, new(mip, 1, layer, 1));
            }
        }
        builder.AddComputePass("read").Read(texture, RenderGraphTextureUsage.TextureBinding).MarkSideEffect();
        var definition = builder.Build();

        if (gap) {
            Assert.Throws<RenderGraphCompilationException>(() => RenderGraphCompiler.Compile(definition));
        }
        else {
            Assert.Equal(17, RenderGraphCompiler.Compile(definition).Passes.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthStencilCoverage_RequiresBothAspectsForWholeReads(bool gap)
    {
        var builder = new RenderGraphBuilder();
        var texture = builder.CreateTexture(new("depth stencil",
            RenderGraphTextureFormat.Depth24PlusStencil8, 16, 16));
        builder.AddPass("depth").Write(texture, RenderGraphTextureUsage.RenderAttachment,
            new(0, 1, 0, 1, RenderGraphTextureAspect.DepthOnly));
        if (!gap) {
            builder.AddPass("stencil").Write(texture, RenderGraphTextureUsage.RenderAttachment,
                new(0, 1, 0, 1, RenderGraphTextureAspect.StencilOnly));
        }
        builder.AddComputePass("read").Read(texture, RenderGraphTextureUsage.TextureBinding).MarkSideEffect();
        var definition = builder.Build();

        if (gap) {
            Assert.Throws<RenderGraphCompilationException>(() => RenderGraphCompiler.Compile(definition));
        }
        else {
            Assert.Equal(3, RenderGraphCompiler.Compile(definition).Passes.Count);
        }
    }
}

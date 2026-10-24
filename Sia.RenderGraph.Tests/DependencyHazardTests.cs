namespace Sia.RenderGraph.Tests;

public sealed class DependencyHazardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteAfterIndependentReadersWaitsForBothReaders(bool texture)
    {
        var builder = new RenderGraphBuilder();
        var buffer = builder.ImportBuffer(new("buffer", 64,
            RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopyDestination));
        var image = builder.ImportTexture(new("image", RenderGraphTextureFormat.RGBA8Unorm, 8, 8,
            usage: RenderGraphTextureUsage.TextureBinding | RenderGraphTextureUsage.CopyDestination));
        var first = builder.AddComputePass("first read");
        var second = builder.AddComputePass("second read");
        var write = builder.AddComputePass("overwrite").MarkSideEffect();
        if (texture) {
            first.Read(image, RenderGraphTextureUsage.TextureBinding);
            second.Read(image, RenderGraphTextureUsage.TextureBinding);
            write.Write(image, RenderGraphTextureUsage.CopyDestination);
        }
        else {
            first.Read(buffer, RenderGraphBufferUsage.Storage);
            second.Read(buffer, RenderGraphBufferUsage.Storage);
            write.Write(buffer, RenderGraphBufferUsage.CopyDestination);
        }
        var compiled = RenderGraphCompiler.Compile(builder.Build());
        Assert.Empty(compiled.GetPass(first.Handle).Dependencies);
        Assert.Empty(compiled.GetPass(second.Handle).Dependencies);
        Assert.Equal(new[] { first.Handle, second.Handle }, compiled.GetPass(write.Handle).Dependencies);
    }
}

namespace Sia.RenderGraph.Tests;

public sealed class AttachmentGroupingTests
{
    [Fact]
    public void LargePureAttachmentChainHasOneGroupWithoutQuadraticAllocation()
    {
        var small = MeasurePureChain(64);
        var large = MeasurePureChain(512);
        Assert.True(large < small * 12, $"Attachment allocation grew faster than pass count: {small} -> {large}.");
    }

    [Fact]
    public void MixedReadThenWriteSplitsAnOtherwiseCompatibleGroup()
    {
        var builder = new RenderGraphBuilder();
        var attachment = builder.CreateTexture(new("attachment", RenderGraphTextureFormat.RGBA8Unorm, 8, 8));
        var other = builder.ImportTexture(new("other", RenderGraphTextureFormat.RGBA8Unorm, 8, 8,
            usage: RenderGraphTextureUsage.TextureBinding | RenderGraphTextureUsage.CopyDestination));
        builder.AddPass("pure").Write(attachment, RenderGraphTextureUsage.RenderAttachment).MarkSideEffect();
        builder.AddPass("mixed read").Write(attachment, RenderGraphTextureUsage.RenderAttachment)
            .Read(other, RenderGraphTextureUsage.TextureBinding).MarkSideEffect();
        builder.AddPass("mixed write").Write(attachment, RenderGraphTextureUsage.RenderAttachment)
            .Write(other, RenderGraphTextureUsage.CopyDestination).MarkSideEffect();
        var compiled = RenderGraphCompiler.Compile(builder.Build());
        Assert.Equal(2, compiled.PassGroups.Count);
        Assert.Equal(2, compiled.PassGroups[0].Count);
        Assert.Equal(1, compiled.PassGroups[1].Count);
    }

    [Fact]
    public void BufferHazardSplitsAttachmentPasses()
    {
        var builder = new RenderGraphBuilder();
        var attachment = builder.CreateTexture(new("attachment", RenderGraphTextureFormat.RGBA8Unorm, 8, 8));
        var buffer = builder.CreateBuffer(new("scratch", 16));
        builder.AddPass("write").Write(attachment, RenderGraphTextureUsage.RenderAttachment)
            .Write(buffer, RenderGraphBufferUsage.Storage).MarkSideEffect();
        builder.AddPass("read").Write(attachment, RenderGraphTextureUsage.RenderAttachment)
            .Read(buffer, RenderGraphBufferUsage.Storage).MarkSideEffect();
        Assert.Equal(2, RenderGraphCompiler.Compile(builder.Build()).PassGroups.Count);
    }

    [Fact]
    public void DifferentAttachmentSetsStartDifferentGroups()
    {
        var builder = new RenderGraphBuilder();
        var first = builder.CreateTexture(new("first", RenderGraphTextureFormat.RGBA8Unorm, 8, 8));
        var second = builder.CreateTexture(new("second", RenderGraphTextureFormat.RGBA8Unorm, 8, 8));
        builder.AddPass("first").Write(first, RenderGraphTextureUsage.RenderAttachment).MarkSideEffect();
        builder.AddPass("second").Write(second, RenderGraphTextureUsage.RenderAttachment).MarkSideEffect();
        Assert.Equal(2, RenderGraphCompiler.Compile(builder.Build()).PassGroups.Count);
    }

    [Fact]
    public void AdditionalSampledUsageOnAnAttachmentPreventsFusion()
    {
        var builder = new RenderGraphBuilder();
        var attachment = builder.ImportTexture(new("attachment", RenderGraphTextureFormat.RGBA8Unorm, 8, 8,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding));
        builder.AddPass("pure before").Write(attachment, RenderGraphTextureUsage.RenderAttachment).MarkSideEffect();
        builder.AddPass("sampled attachment").Write(attachment, RenderGraphTextureUsage.RenderAttachment)
            .Read(attachment, RenderGraphTextureUsage.TextureBinding).MarkSideEffect();
        builder.AddPass("pure after").Write(attachment, RenderGraphTextureUsage.RenderAttachment).MarkSideEffect();
        Assert.Equal(3, RenderGraphCompiler.Compile(builder.Build()).PassGroups.Count);
    }

    [Fact]
    public void SharedReadonlyResourcesDoNotPreventAttachmentFusion()
    {
        var builder = new RenderGraphBuilder();
        var attachment = builder.CreateTexture(new("attachment", RenderGraphTextureFormat.RGBA8Unorm, 8, 8));
        var sampled = builder.ImportTexture(new("sampled", RenderGraphTextureFormat.RGBA8Unorm, 8, 8,
            usage: RenderGraphTextureUsage.TextureBinding));
        var constants = builder.ImportBuffer(new("constants", 64, RenderGraphBufferUsage.Uniform));
        for (var i = 0; i < 64; i++) {
            builder.AddPass($"draw {i}").Write(attachment, RenderGraphTextureUsage.RenderAttachment)
                .Read(sampled, RenderGraphTextureUsage.TextureBinding)
                .Read(constants, RenderGraphBufferUsage.Uniform).MarkSideEffect();
        }
        Assert.Single(RenderGraphCompiler.Compile(builder.Build()).PassGroups);
    }

    private static long MeasurePureChain(int count)
    {
        var builder = new RenderGraphBuilder();
        var attachment = builder.CreateTexture(new("attachment", RenderGraphTextureFormat.RGBA8Unorm, 8, 8));
        for (var i = 0; i < count; i++) {
            builder.AddPass($"pass {i}").Write(attachment, RenderGraphTextureUsage.RenderAttachment).MarkSideEffect();
        }
        var definition = builder.Build();
        _ = RenderGraphCompiler.Compile(definition);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var compiled = RenderGraphCompiler.Compile(definition);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count, compiled.Passes.Count);
        Assert.Single(compiled.PassGroups);
        return allocated;
    }
}

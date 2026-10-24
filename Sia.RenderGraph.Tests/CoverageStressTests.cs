namespace Sia.RenderGraph.Tests;

public class CoverageStressTests
{
    [Fact]
    public void TextureValidationDoesNotAllocatePerMipOrArrayLayer()
    {
        var small = TextureReads(1, 1);
        var large = TextureReads(7, 8);
        for (var i = 0; i < 4; i++) {
            RenderGraphCompiler.Compile(small);
            RenderGraphCompiler.Compile(large);
        }
        var smallBytes = AllocatedForCompile(small);
        var largeBytes = AllocatedForCompile(large);

        Assert.True(largeBytes <= smallBytes + 1_024,
            $"Subresource validation allocated per cell: small={smallBytes}, large={largeBytes}.");
    }

    [Fact]
    public void BufferCoverageMatchesAnIndependentByteCoverageOracle()
    {
        var random = new Random(814);
        for (var sample = 0; sample < 128; sample++) {
            var builder = new RenderGraphBuilder();
            var buffer = builder.CreateBuffer(new("buffer", 64));
            var covered = new bool[64];
            for (var write = 0; write < 12; write++) {
                var start = random.Next(64);
                var count = random.Next(1, 65 - start);
                Array.Fill(covered, true, start, count);
                builder.AddComputePass($"write {write}").Write(buffer, RenderGraphBufferUsage.Storage,
                    new((ulong)start, (ulong)count));
            }
            var readStart = random.Next(64);
            var readCount = random.Next(1, 65 - readStart);
            var expected = covered.Skip(readStart).Take(readCount).All(value => value);
            builder.AddComputePass("read").Read(buffer, RenderGraphBufferUsage.Storage,
                new((ulong)readStart, (ulong)readCount)).MarkSideEffect();
            var definition = builder.Build();
            if (expected) {
                Assert.NotEmpty(RenderGraphCompiler.Compile(definition).Passes);
            }
            else {
                Assert.Throws<RenderGraphCompilationException>(() => RenderGraphCompiler.Compile(definition));
            }
        }
    }

    private static long AllocatedForCompile(RenderGraphDefinition definition)
    {
        // Amortize one-off runtime/test-host allocations over a fixed batch.
        // The per-compilation budget above stays unchanged.
        const int compilations = 16;
        CompiledRenderGraph? compiled = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < compilations; i++) {
            compiled = RenderGraphCompiler.Compile(definition);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(64, compiled!.Passes.Count);
        return allocated / compilations;
    }

    private static RenderGraphDefinition TextureReads(uint mips, uint layers)
    {
        var builder = new RenderGraphBuilder();
        var texture = builder.CreateTexture(new("texture", RenderGraphTextureFormat.RGBA8Unorm,
            128, 128, layers, mips));
        builder.AddComputePass("write").Write(texture, RenderGraphTextureUsage.CopyDestination);
        for (var i = 1; i < 64; i++) {
            builder.AddComputePass($"read {i}").Read(texture, RenderGraphTextureUsage.TextureBinding).MarkSideEffect();
        }
        return builder.Build();
    }
}

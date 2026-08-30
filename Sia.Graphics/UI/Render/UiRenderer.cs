using Sia;
using Sia.Graphics.Reactive;
using Sia.Graphics.Text;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public sealed class UiRenderer(UiPipeline pipeline)
{
    private Entity _bindGroup;
    private int _boundTextureVersion = -1;
    private long _uploadedVersion = -1;
    private Size? _uploadedViewport;

    public void Render(
        World world,
        WgpuReactiveRenderGraphPassContext context,
        RenderGraphTextureKey output,
        Size viewport,
        WGPULoadOp loadOp = WGPULoadOp.Load,
        bool outputCacheable = true)
    {
        var primitiveCount = PrepareFrame(world, viewport);
        var renderPass = context.GetOrBeginRenderPass(
            new WgpuReactiveRenderGraphColorAttachment(output, loadOp, Cacheable: outputCacheable));
        Encode(renderPass, primitiveCount);
    }

    public uint PrepareFrame(World world, Size viewport)
    {
        var cache = world.AcquireAddon<UiRenderCache>();
        cache.Prepare();
        var queue = pipeline.Queue.GetWgpu<WGPUQueue>();

        if (_uploadedViewport != viewport) {
            Wgpu.WriteBuffer<float>(
                queue,
                pipeline.ViewUniformBuffer.GetWgpu<WGPUBuffer>(),
                0,
                UiOrthographicProjection.Build(viewport));
            _uploadedViewport = viewport;
        }

        var vertexSource = pipeline.VertexSource;
        var invalidateBindGroup = _uploadedVersion != cache.PreparedVersion
            ? vertexSource.UploadFrame(world, pipeline.Device, queue, cache)
            : vertexSource.EnsureBuffers(world, pipeline.Device);
        _uploadedVersion = cache.PreparedVersion;
        if (invalidateBindGroup && _bindGroup.IsValid)
            _bindGroup.Destroy();

        pipeline.UploadAtlases(world, world.AcquireAddon<FontAtlasSet>());
        EnsureBindGroup(world);
        return (uint)cache.PaintOrder.Count;
    }

    private void EnsureBindGroup(World world)
    {
        if (_bindGroup.IsValid && _boundTextureVersion == pipeline.TextureVersion)
            return;
        if (_bindGroup.IsValid)
            _bindGroup.Destroy();
        _bindGroup = world.OwnWgpu(pipeline.CreateBindGroup());
        _boundTextureVersion = pipeline.TextureVersion;
    }

    public void Encode(WgpuHandle<WGPURenderPassEncoder> renderPass, uint primitiveCount)
    {
        Wgpu.SetRenderPipeline(renderPass, pipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, _bindGroup.GetWgpu<WGPUBindGroup>());
        pipeline.VertexSource.BindForDraw(renderPass);
        if (primitiveCount > 0)
            Wgpu.Draw(renderPass, 6, primitiveCount);
    }
}

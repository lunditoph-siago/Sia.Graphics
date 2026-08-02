using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphBindings
{
    private readonly Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> _buffers = [];
    private readonly Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> _textures = [];
    private readonly Dictionary<RenderGraphPassHandle, WgpuRenderGraphPassHandler> _handlers = [];

    public WgpuRenderGraphBindings(WgpuRenderGraphPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
    }

    public WgpuRenderGraphPlan Plan { get; }

    public void Bind(
        RenderGraphBufferHandle buffer,
        WgpuHandle<WGPUBuffer> handle)
    {
        var resource = Plan.Graph.GetBuffer(buffer);
        if (!resource.IsImported) {
            throw new ArgumentException(
                "Only imported render graph buffers can be bound.",
                nameof(buffer));
        }
        if (handle.IsNull) {
            throw new ArgumentException(
                "An imported render graph buffer cannot use a null handle.",
                nameof(handle));
        }

        _buffers[buffer] = handle;
    }

    public void Bind(
        RenderGraphTextureHandle texture,
        WgpuHandle<WGPUTexture> handle)
    {
        var resource = Plan.Graph.GetTexture(texture);
        if (!resource.IsImported) {
            throw new ArgumentException(
                "Only imported render graph textures can be bound.",
                nameof(texture));
        }
        if (handle.IsNull) {
            throw new ArgumentException(
                "An imported render graph texture cannot use a null handle.",
                nameof(handle));
        }

        _textures[texture] = handle;
    }

    public void SetPassHandler(
        RenderGraphPassHandle pass,
        WgpuRenderGraphPassHandler handler)
    {
        Plan.Graph.GetPass(pass);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[pass] = handler;
    }

    internal bool TryGetBuffer(
        RenderGraphBufferHandle buffer,
        out WgpuHandle<WGPUBuffer> handle) =>
        _buffers.TryGetValue(buffer, out handle);

    internal bool TryGetTexture(
        RenderGraphTextureHandle texture,
        out WgpuHandle<WGPUTexture> handle) =>
        _textures.TryGetValue(texture, out handle);

    internal bool TryGetHandler(
        RenderGraphPassHandle pass,
        out WgpuRenderGraphPassHandler? handler) =>
        _handlers.TryGetValue(pass, out handler);
}

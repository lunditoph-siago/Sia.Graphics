using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphBindings
{
    private readonly Dictionary<RenderGraphBufferHandle, Binding<WGPUBuffer>> _buffers = [];
    private readonly Dictionary<RenderGraphTextureHandle, Binding<WGPUTexture>> _textures = [];
    private readonly Dictionary<RenderGraphPassHandle, WgpuRenderGraphPassHandler> _handlers = [];

    private readonly record struct Binding<T>(WgpuHandle<T> Handle, bool Validated = false)
        where T : unmanaged;

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

        _buffers[buffer] = new(handle);
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

        _textures[texture] = new(handle);
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
        out WgpuHandle<WGPUBuffer> handle,
        out bool validated)
    {
        var found = _buffers.TryGetValue(buffer, out var binding);
        handle = binding.Handle;
        validated = binding.Validated;
        return found;
    }

    internal bool TryGetTexture(
        RenderGraphTextureHandle texture,
        out WgpuHandle<WGPUTexture> handle,
        out bool validated)
    {
        var found = _textures.TryGetValue(texture, out var binding);
        handle = binding.Handle;
        validated = binding.Validated;
        return found;
    }

    internal void MarkValidated(RenderGraphBufferHandle buffer) =>
        _buffers[buffer] = _buffers[buffer] with { Validated = true };

    internal void MarkValidated(RenderGraphTextureHandle texture) =>
        _textures[texture] = _textures[texture] with { Validated = true };

    internal bool TryGetHandler(
        RenderGraphPassHandle pass,
        out WgpuRenderGraphPassHandler? handler) =>
        _handlers.TryGetValue(pass, out handler);

    public void Clear()
    {
        _buffers.Clear();
        _textures.Clear();
        _handlers.Clear();
    }
}

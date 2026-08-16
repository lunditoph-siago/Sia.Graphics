using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public sealed class WgpuReactiveRenderGraphPassContext
{
    private readonly IReadOnlyDictionary<RenderGraphBufferKey, RenderGraphBufferHandle>
        _buffers;
    private readonly IReadOnlyDictionary<RenderGraphTextureKey, RenderGraphTextureHandle>
        _textures;
    private WgpuRenderGraphPassContext? _context;

    internal WgpuReactiveRenderGraphPassContext(
        IReadOnlyDictionary<RenderGraphBufferKey, RenderGraphBufferHandle> buffers,
        IReadOnlyDictionary<RenderGraphTextureKey, RenderGraphTextureHandle> textures)
    {
        _buffers = buffers;
        _textures = textures;
    }

    public CompiledRenderGraphPass Pass => Current.Pass;

    public WgpuHandle<WGPUCommandEncoder> CommandEncoder => Current.CommandEncoder;

    public WgpuHandle<WGPUBuffer> GetBuffer(RenderGraphBufferKey key) =>
        Current.GetBuffer(GetBufferHandle(key));

    public WgpuHandle<WGPUTexture> GetTexture(RenderGraphTextureKey key) =>
        Current.GetTexture(GetTextureHandle(key));

    public WgpuHandle<WGPUTextureView> GetTextureView(
        RenderGraphTextureKey key,
        bool cacheable = true) =>
        Current.GetTextureView(GetTextureHandle(key), cacheable);

    public WgpuHandle<WGPUTextureView> GetTextureView(
        RenderGraphTextureKey key,
        RenderGraphTextureSubresourceRange subresources,
        bool cacheable = true) =>
        Current.GetTextureView(GetTextureHandle(key), subresources, cacheable);

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        WgpuReactiveRenderGraphColorAttachment colorAttachment) =>
        GetOrBeginRenderPass(colorAttachment, depthStencilAttachment: null);

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        WgpuReactiveRenderGraphColorAttachment colorAttachment,
        WgpuReactiveRenderGraphDepthStencilAttachment? depthStencilAttachment) =>
        Current.GetOrBeginRenderPass(
            new WgpuRenderGraphColorAttachment(
                GetTextureHandle(colorAttachment.Texture),
                colorAttachment.LoadOp,
                colorAttachment.StoreOp,
                colorAttachment.ClearValue,
                colorAttachment.Subresources,
                colorAttachment.Cacheable),
            Lower(depthStencilAttachment));

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        WgpuReactiveRenderGraphDepthStencilAttachment depthStencilAttachment) =>
        Current.GetOrBeginRenderPass(Lower(depthStencilAttachment)!.Value);

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        ReadOnlySpan<WgpuReactiveRenderGraphColorAttachment> colorAttachments) =>
        GetOrBeginRenderPass(colorAttachments, depthStencilAttachment: null);

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        ReadOnlySpan<WgpuReactiveRenderGraphColorAttachment> colorAttachments,
        WgpuReactiveRenderGraphDepthStencilAttachment? depthStencilAttachment)
    {
        Span<WgpuRenderGraphColorAttachment> lowered =
            colorAttachments.Length <= 8
                ? stackalloc WgpuRenderGraphColorAttachment[colorAttachments.Length]
                : new WgpuRenderGraphColorAttachment[colorAttachments.Length];
        for (var index = 0; index < colorAttachments.Length; index++) {
            var attachment = colorAttachments[index];
            lowered[index] = new WgpuRenderGraphColorAttachment(
                GetTextureHandle(attachment.Texture),
                attachment.LoadOp,
                attachment.StoreOp,
                attachment.ClearValue,
                attachment.Subresources,
                attachment.Cacheable);
        }

        return Current.GetOrBeginRenderPass(lowered, Lower(depthStencilAttachment));
    }

    private WgpuRenderGraphDepthStencilAttachment? Lower(
        WgpuReactiveRenderGraphDepthStencilAttachment? depthStencilAttachment) =>
        depthStencilAttachment is { } depthStencil
            ? new WgpuRenderGraphDepthStencilAttachment(
                GetTextureHandle(depthStencil.Texture),
                depthStencil.DepthLoadOp,
                depthStencil.DepthStoreOp,
                depthStencil.DepthClearValue,
                depthStencil.DepthReadOnly,
                depthStencil.StencilLoadOp,
                depthStencil.StencilStoreOp,
                depthStencil.StencilClearValue,
                depthStencil.StencilReadOnly,
                depthStencil.Subresources,
                depthStencil.Cacheable)
            : null;

    public WgpuHandle<WGPUComputePassEncoder> GetOrBeginComputePass() =>
        Current.GetOrBeginComputePass();

    internal void Begin(WgpuRenderGraphPassContext context)
    {
        if (_context != null) {
            throw new InvalidOperationException(
                "A reactive render graph pass handler cannot be entered recursively.");
        }
        _context = context;
    }

    internal void End() => _context = null;

    private WgpuRenderGraphPassContext Current =>
        _context ?? throw new InvalidOperationException(
            "The reactive render graph pass context is only valid while its handler executes.");

    private RenderGraphBufferHandle GetBufferHandle(RenderGraphBufferKey key) =>
        _buffers.TryGetValue(key, out var handle)
            ? handle
            : throw new KeyNotFoundException(
                $"Render graph buffer '{key}' is not registered.");

    private RenderGraphTextureHandle GetTextureHandle(RenderGraphTextureKey key) =>
        _textures.TryGetValue(key, out var handle)
            ? handle
            : throw new KeyNotFoundException(
                $"Render graph texture '{key}' is not registered.");
}

internal sealed class ReactiveRenderGraphPassAdapter
{
    private readonly WgpuReactiveRenderGraphPassHandler _handler;
    private readonly WgpuReactiveRenderGraphPassContext _context;

    public ReactiveRenderGraphPassAdapter(
        WgpuReactiveRenderGraphPassHandler handler,
        IReadOnlyDictionary<RenderGraphBufferKey, RenderGraphBufferHandle> buffers,
        IReadOnlyDictionary<RenderGraphTextureKey, RenderGraphTextureHandle> textures)
    {
        _handler = handler;
        _context = new(buffers, textures);
        Handler = Execute;
    }

    public WgpuRenderGraphPassHandler Handler { get; }

    public void Execute(WgpuRenderGraphPassContext context)
    {
        _context.Begin(context);
        try {
            _handler(_context);
        }
        finally {
            _context.End();
        }
    }
}

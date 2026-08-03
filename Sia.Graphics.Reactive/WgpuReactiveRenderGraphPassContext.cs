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

    public WgpuHandle<WGPUTextureView> GetTextureView(RenderGraphTextureKey key) =>
        Current.GetTextureView(GetTextureHandle(key));

    public WgpuHandle<WGPUTextureView> GetTextureView(
        RenderGraphTextureKey key,
        RenderGraphTextureSubresourceRange subresources) =>
        Current.GetTextureView(GetTextureHandle(key), subresources);

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
    }

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

using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphExports : IDisposable
{
    private readonly WgpuRenderGraphPlan _plan;
    private readonly Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> _buffers = [];
    private readonly Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> _textures = [];
    private readonly HashSet<RenderGraphBufferHandle> _ownedBuffers = [];
    private readonly HashSet<RenderGraphTextureHandle> _ownedTextures = [];
    private bool _disposed;

    internal WgpuRenderGraphExports(WgpuRenderGraphPlan plan)
    {
        _plan = plan;
    }

    public WgpuHandle<WGPUBuffer> GetBuffer(RenderGraphBufferHandle buffer)
    {
        ThrowIfDisposed();
        if (!_plan.Graph.IsExported(buffer) || !_buffers.TryGetValue(buffer, out var handle)) {
            throw new ArgumentException(
                "The buffer was not exported by this render graph execution.",
                nameof(buffer));
        }

        return handle;
    }

    public WgpuHandle<WGPUTexture> GetTexture(RenderGraphTextureHandle texture)
    {
        ThrowIfDisposed();
        if (!_plan.Graph.IsExported(texture) ||
            !_textures.TryGetValue(texture, out var handle)) {
            throw new ArgumentException(
                "The texture was not exported by this render graph execution.",
                nameof(texture));
        }

        return handle;
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }

        _disposed = true;
        foreach (var buffer in _ownedBuffers) {
            var handle = _buffers[buffer];
            Wgpu.Release(ref handle);
        }
        foreach (var texture in _ownedTextures) {
            var handle = _textures[texture];
            Wgpu.Release(ref handle);
        }

        _buffers.Clear();
        _textures.Clear();
        _ownedBuffers.Clear();
        _ownedTextures.Clear();
    }

    internal void Add(
        RenderGraphBufferHandle buffer,
        WgpuHandle<WGPUBuffer> handle,
        bool ownsHandle)
    {
        _buffers.Add(buffer, handle);
        if (ownsHandle) {
            _ownedBuffers.Add(buffer);
        }
    }

    internal void Add(
        RenderGraphTextureHandle texture,
        WgpuHandle<WGPUTexture> handle,
        bool ownsHandle)
    {
        _textures.Add(texture, handle);
        if (ownsHandle) {
            _ownedTextures.Add(texture);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

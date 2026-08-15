namespace Sia.WebGPU;

/// <summary>
/// Caches <see cref="WGPUTextureView"/> objects created for render graph passes across
/// executions, keyed by the owning texture and the requested view range. Entries that were
/// not requested during a call to <see cref="EndFrame"/>'s preceding execution are released,
/// so a texture that stops being bound (e.g. after a resize) is cleaned up automatically
/// without the executor having to track its identity explicitly.
/// </summary>
public sealed class WgpuRenderGraphViewCache : IDisposable
{
    private readonly record struct Key(
        WgpuHandle<WGPUTexture> Texture,
        WGPUTextureFormat Format,
        WGPUTextureViewDimension Dimension,
        uint BaseMipLevel,
        uint MipLevelCount,
        uint BaseArrayLayer,
        uint ArrayLayerCount,
        WGPUTextureAspect Aspect,
        WGPUTextureUsage Usage);

    private readonly Dictionary<Key, WgpuHandle<WGPUTextureView>> _views = [];
    private readonly HashSet<Key> _usedThisFrame = [];
    private bool _disposed;

    public WgpuHandle<WGPUTextureView> GetOrCreate(
        WgpuHandle<WGPUTexture> texture,
        in WGPUTextureViewDescriptor descriptor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = new Key(
            texture,
            descriptor.Format,
            descriptor.Dimension,
            descriptor.BaseMipLevel,
            descriptor.MipLevelCount,
            descriptor.BaseArrayLayer,
            descriptor.ArrayLayerCount,
            descriptor.Aspect,
            descriptor.Usage);
        _usedThisFrame.Add(key);

        if (_views.TryGetValue(key, out var view)) {
            return view;
        }

        view = Wgpu.CreateTextureView(texture, in descriptor);
        if (view.IsNull) {
            throw new InvalidOperationException(
                "WebGPU could not create a cached texture view.");
        }

        _views.Add(key, view);
        return view;
    }

    /// <summary>
    /// Releases every cached view that was not requested since the previous call to
    /// <see cref="EndFrame"/>. Call once after each render graph execution.
    /// </summary>
    public void EndFrame()
    {
        if (_usedThisFrame.Count < _views.Count) {
            foreach (var key in _views.Keys.Except(_usedThisFrame).ToArray()) {
                var view = _views[key];
                Wgpu.Release(ref view);
                _views.Remove(key);
            }
        }

        _usedThisFrame.Clear();
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var key in _views.Keys.ToArray()) {
            var view = _views[key];
            Wgpu.Release(ref view);
        }
        _views.Clear();
        _usedThisFrame.Clear();
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;

        foreach (var key in _views.Keys.ToArray()) {
            var view = _views[key];
            Wgpu.Release(ref view);
        }
        _views.Clear();
        _usedThisFrame.Clear();
    }
}

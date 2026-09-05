using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphResourcePool : IDisposable
{
    private readonly Dictionary<BufferKey, List<BufferEntry>> _buffers = [];
    private readonly Dictionary<TextureKey, List<TextureEntry>> _textures = [];
    private readonly int _maxResourcesPerDescriptor;
    private readonly ulong _maxIdleFrames;
    private WgpuHandle<WGPUDevice> _device;
    private ulong _frameIndex;
    private bool _disposed;

    public WgpuRenderGraphResourcePool(
        int maxResourcesPerDescriptor = 8,
        ulong maxIdleFrames = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResourcesPerDescriptor);
        _maxResourcesPerDescriptor = maxResourcesPerDescriptor;
        _maxIdleFrames = maxIdleFrames;
    }

    public ulong CreatedBuffers { get; private set; }

    public ulong CreatedTextures { get; private set; }

    public ulong ReusedBuffers { get; private set; }

    public ulong ReusedTextures { get; private set; }

    public WgpuRenderGraphResourcePoolStats Stats => new(
        _buffers.Values.Sum(static entries => entries.Count),
        _textures.Values.Sum(static entries => entries.Count),
        CreatedBuffers,
        CreatedTextures,
        ReusedBuffers,
        ReusedTextures);

    internal void BeginFrame(WgpuHandle<WGPUDevice> device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (device.IsNull) {
            throw new ArgumentException("The WebGPU device is null.", nameof(device));
        }
        if (!_device.IsNull && _device != device) {
            Clear();
        }
        _device = device;
        _frameIndex++;
        Trim();
    }

    internal WgpuHandle<WGPUBuffer> RentBuffer(
        WgpuHandle<WGPUDevice> device,
        in RenderGraphBufferDescriptor resource,
        WGPUBufferUsage usage)
    {
        var key = new BufferKey(resource.Size, usage);
        if (_buffers.TryGetValue(key, out var available) && available.Count != 0) {
            var last = available.Count - 1;
            var entry = available[last];
            available.RemoveAt(last);
            ReusedBuffers++;
            return entry.Handle;
        }

        using var label = WgpuOwnedString.Create(resource.Name);
        var descriptor = WGPUBufferDescriptor.Default;
        descriptor.Label = label.View;
        descriptor.Size = resource.Size;
        descriptor.Usage = usage;
        var buffer = Wgpu.CreateBuffer(device, in descriptor);
        if (buffer.IsNull) {
            throw new InvalidOperationException(
                $"WebGPU could not create buffer '{resource.Name}'.");
        }
        CreatedBuffers++;
        return buffer;
    }

    internal WgpuHandle<WGPUTexture> RentTexture(
        WgpuHandle<WGPUDevice> device,
        in RenderGraphTextureDescriptor resource,
        WGPUTextureDimension dimension,
        WGPUTextureFormat format,
        WGPUTextureUsage usage)
    {
        var key = new TextureKey(
            resource.Width,
            resource.Height,
            resource.DepthOrArrayLayers,
            resource.MipLevelCount,
            resource.SampleCount,
            dimension,
            format,
            usage);
        if (_textures.TryGetValue(key, out var available) && available.Count != 0) {
            var last = available.Count - 1;
            var entry = available[last];
            available.RemoveAt(last);
            ReusedTextures++;
            return entry.Handle;
        }

        using var label = WgpuOwnedString.Create(resource.Name);
        var descriptor = WGPUTextureDescriptor.Default;
        descriptor.Label = label.View;
        descriptor.Usage = usage;
        descriptor.Dimension = dimension;
        descriptor.Size = new WGPUExtent3D {
            Width = resource.Width,
            Height = resource.Height,
            DepthOrArrayLayers = resource.DepthOrArrayLayers,
        };
        descriptor.Format = format;
        descriptor.MipLevelCount = resource.MipLevelCount;
        descriptor.SampleCount = resource.SampleCount;
        var texture = Wgpu.CreateTexture(device, in descriptor);
        if (texture.IsNull) {
            throw new InvalidOperationException(
                $"WebGPU could not create texture '{resource.Name}'.");
        }
        CreatedTextures++;
        return texture;
    }

    internal void ReturnBuffer(
        in RenderGraphBufferDescriptor resource,
        WGPUBufferUsage usage,
        WgpuHandle<WGPUBuffer> buffer)
    {
        if (_disposed || buffer.IsNull) {
            Wgpu.Release(ref buffer);
            return;
        }
        var key = new BufferKey(resource.Size, usage);
        var available = GetOrAdd(_buffers, key);
        if (available.Count >= _maxResourcesPerDescriptor) {
            Wgpu.Release(ref buffer);
            return;
        }
        available.Add(new BufferEntry(buffer, _frameIndex));
    }

    internal void ReturnTexture(
        in RenderGraphTextureDescriptor resource,
        WGPUTextureDimension dimension,
        WGPUTextureFormat format,
        WGPUTextureUsage usage,
        WgpuHandle<WGPUTexture> texture)
    {
        if (_disposed || texture.IsNull) {
            Wgpu.Release(ref texture);
            return;
        }
        var key = new TextureKey(
            resource.Width,
            resource.Height,
            resource.DepthOrArrayLayers,
            resource.MipLevelCount,
            resource.SampleCount,
            dimension,
            format,
            usage);
        var available = GetOrAdd(_textures, key);
        if (available.Count >= _maxResourcesPerDescriptor) {
            Wgpu.Release(ref texture);
            return;
        }
        available.Add(new TextureEntry(texture, _frameIndex));
    }

    public void Trim()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Trim(_buffers, _frameIndex, _maxIdleFrames, static entry => entry.LastUsed, Release);
        Trim(_textures, _frameIndex, _maxIdleFrames, static entry => entry.LastUsed, Release);
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseAll();
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        ReleaseAll();
        _disposed = true;
    }

    private void ReleaseAll()
    {
        foreach (var entries in _buffers.Values) {
            foreach (var entry in entries) {
                var handle = entry.Handle;
                Wgpu.Release(ref handle);
            }
        }
        foreach (var entries in _textures.Values) {
            foreach (var entry in entries) {
                var handle = entry.Handle;
                Wgpu.Release(ref handle);
            }
        }
        _buffers.Clear();
        _textures.Clear();
    }

    private static List<TEntry> GetOrAdd<TKey, TEntry>(
        Dictionary<TKey, List<TEntry>> entries,
        TKey key)
        where TKey : notnull
    {
        if (!entries.TryGetValue(key, out var available)) {
            available = [];
            entries.Add(key, available);
        }
        return available;
    }

    private static void Trim<TKey, TEntry>(
        Dictionary<TKey, List<TEntry>> entries,
        ulong frameIndex,
        ulong maxIdleFrames,
        Func<TEntry, ulong> getLastUsed,
        Action<TEntry> release)
        where TKey : notnull
    {
        foreach (var (key, available) in entries.ToArray()) {
            for (var index = available.Count - 1; index >= 0; index--) {
                var entry = available[index];
                if (frameIndex - getLastUsed(entry) <= maxIdleFrames) {
                    continue;
                }
                available.RemoveAt(index);
                release(entry);
            }
            if (available.Count == 0) {
                entries.Remove(key);
            }
        }
    }

    private static void Release(BufferEntry entry)
    {
        var handle = entry.Handle;
        Wgpu.Release(ref handle);
    }

    private static void Release(TextureEntry entry)
    {
        var handle = entry.Handle;
        Wgpu.Release(ref handle);
    }

    private readonly record struct BufferKey(ulong Size, WGPUBufferUsage Usage);

    private readonly record struct TextureKey(
        uint Width,
        uint Height,
        uint DepthOrArrayLayers,
        uint MipLevelCount,
        uint SampleCount,
        WGPUTextureDimension Dimension,
        WGPUTextureFormat Format,
        WGPUTextureUsage Usage);

    private readonly record struct BufferEntry(WgpuHandle<WGPUBuffer> Handle, ulong LastUsed);

    private readonly record struct TextureEntry(WgpuHandle<WGPUTexture> Handle, ulong LastUsed);
}

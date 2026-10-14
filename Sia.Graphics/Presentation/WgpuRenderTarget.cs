using Sia.WebGPU;

namespace Sia.Graphics;

public sealed partial class WgpuRenderTarget : IDisposable
{
    private readonly WgpuHandle<WGPUDevice> _device;
    private readonly WgpuHandle<WGPUQueue> _queue;
    private readonly WGPUCompositeAlphaMode _alphaMode;
    private readonly WGPUPresentMode _presentMode;
    private readonly uint _maxDimension;
    private WgpuHandle<WGPUSurface> _surface;
    private WgpuHandle<WGPUTexture> _frameTexture;
    private long _generation;
    private bool _active, _presented, _configured, _disposed;

    private WgpuRenderTarget(WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue,
        WgpuHandle<WGPUSurface> surface, WGPUTextureFormat format,
        WGPUCompositeAlphaMode alphaMode, WGPUPresentMode presentMode)
    {
        if (device.IsNull) throw new ArgumentException("Device is null.", nameof(device));
        if (queue.IsNull) throw new ArgumentException("Queue is null.", nameof(queue));
        _device = device;
        _queue = queue;
        _surface = surface;
        Format = format;
        _alphaMode = alphaMode;
        _presentMode = presentMode;
        _maxDimension = Wgpu.GetLimits(device).MaxTextureDimension2D;
    }

    public WGPUTextureFormat Format { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public static WgpuRenderTarget FromSurface(WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue, WgpuHandle<WGPUSurface> surface, WGPUTextureFormat format,
        WGPUCompositeAlphaMode alphaMode = WGPUCompositeAlphaMode.Opaque,
        WGPUPresentMode presentMode = WGPUPresentMode.Fifo)
    {
        if (surface.IsNull) throw new ArgumentException("Surface is null.", nameof(surface));
        return new(device, queue, surface, format, alphaMode, presentMode);
    }

    public bool TryAcquireFrame(int width, int height, out WgpuRenderTargetFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active) throw new InvalidOperationException("Dispose the previous frame before acquiring another.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((uint)width > _maxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "Target exceeds the device texture dimension limit.");
        if ((uint)height > _maxDimension)
            throw new ArgumentOutOfRangeException(nameof(height), "Target exceeds the device texture dimension limit.");
        frame = default;
#if BROWSER
        CheckCanvasError();
        if (_readbackCanvas && PixelsPending() != 0) return false;
#endif
        if (!_configured || Width != width || Height != height) Configure(width, height);
#if BROWSER
        if (_readbackCanvas) _frameTexture = _outputTexture;
        else
#endif
        {
            var acquired = Wgpu.AcquireSurfaceTexture(_surface);
            if (acquired.Status is not (WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
                or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal) || !acquired.HasTexture) {
                var status = acquired.Status;
                Wgpu.Release(ref acquired);
                if (status is WGPUSurfaceGetCurrentTextureStatus.Outdated or WGPUSurfaceGetCurrentTextureStatus.Lost) {
                    _configured = false;
                    return false;
                }
                if (status == WGPUSurfaceGetCurrentTextureStatus.Timeout) return false;
                throw new WgpuException($"Surface acquisition failed: {status}.");
            }
            _frameTexture = acquired.Texture;
        }
        _active = true;
        _presented = false;
        frame = new(this, ++_generation);
        return true;
    }

    private unsafe void Configure(int width, int height)
    {
#if BROWSER
        if (_readbackCanvas) ResizeReadback(width, height);
        else
#endif
        {
#if BROWSER
            ResizeCanvas(width, height);
#endif
            Wgpu.ConfigureSurface(_surface, new WGPUSurfaceConfiguration {
                Device = (WGPUDevice*)_device.Handle, Format = Format,
                Usage = WGPUTextureUsage.RenderAttachment,
                Width = (uint)width, Height = (uint)height,
                AlphaMode = _alphaMode, PresentMode = _presentMode,
            });
        }
        Width = width;
        Height = height;
        _configured = true;
    }

    internal void RequireFrame(long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_active || generation != _generation)
            throw new InvalidOperationException("The presentation frame has expired.");
        if (_presented) throw new InvalidOperationException("This frame was already presented.");
    }

    internal WgpuHandle<WGPUTexture> GetFrameTexture(long generation)
    {
        RequireFrame(generation);
        return _frameTexture;
    }

    internal void Present(long generation)
    {
        RequireFrame(generation);
#if BROWSER
        if (_readbackCanvas) BeginReadback();
        else
#endif
        Wgpu.PresentSurfaceOrThrow(_surface);
        _presented = true;
    }

    internal void EndFrame(long generation)
    {
        if (!_active || generation != _generation) return;
        _active = false;
#if BROWSER
        if (_readbackCanvas) { _frameTexture = default; return; }
#endif
        Wgpu.Release(ref _frameTexture);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { EndFrame(_generation); }
        finally {
            try {
                if (Width > 0 && !_surface.IsNull) Wgpu.UnconfigureSurface(_surface);
            }
            finally {
#if BROWSER
                DisposeCanvas();
#endif
            }
        }
    }
}

public readonly struct WgpuRenderTargetFrame : IDisposable
{
    private readonly WgpuRenderTarget? _target;
    private readonly long _generation;

    internal WgpuRenderTargetFrame(WgpuRenderTarget target, long generation)
    {
        _target = target;
        _generation = generation;
    }

    private WgpuRenderTarget Target {
        get {
            var target = _target ?? throw new InvalidOperationException("No frame was acquired.");
            target.RequireFrame(_generation);
            return target;
        }
    }

    public WgpuHandle<WGPUTexture> Texture => Target.GetFrameTexture(_generation);
    public WGPUTextureFormat Format => Target.Format;
    public int Width => Target.Width;
    public int Height => Target.Height;
    public void Present() => Target.Present(_generation);
    public void Dispose() => _target?.EndFrame(_generation);
}

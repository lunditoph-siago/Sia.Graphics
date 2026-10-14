#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Sia.WebGPU;

namespace Sia.Graphics;

public sealed partial class WgpuRenderTarget
{
    private byte[]? _selector;
    private bool _readbackCanvas, _ownsSurface;
    private int _canvasToken;
    private nuint _canvasThread;
    private WgpuHandle<WGPUTexture> _outputTexture;
    private WgpuHandle<WGPUBuffer> _readback;
    private int _bytesPerRow;

    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsCanvasThread")]
    private static extern nuint InitializeCanvasWorker();

    [JSImport("globalThis.__siaGraphicsNextTurn")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task NextBrowserTurn();

    public static async Task<WgpuRenderTarget> FromCanvasAsync(WgpuHandle<WGPUInstance> instance,
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue,
        string canvasId, WgpuCanvasPresentation presentation = WgpuCanvasPresentation.Direct,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(canvasId);
        if (!char.IsAsciiLetter(canvasId[0]) || canvasId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Canvas id must start with an ASCII letter and contain only letters, digits, '-' or '_'.", nameof(canvasId));
        if (presentation is not (WgpuCanvasPresentation.Direct or WgpuCanvasPresentation.Readback))
            throw new ArgumentOutOfRangeException(nameof(presentation));
        if (presentation == WgpuCanvasPresentation.Direct && instance.IsNull)
            throw new ArgumentException("Instance is null.", nameof(instance));
        cancellationToken.ThrowIfCancellationRequested();
        var thread = InitializeCanvasWorker();
        if (thread == 0) throw new InvalidOperationException("Canvas targets require a managed render Worker.");
        var readback = presentation == WgpuCanvasPresentation.Readback;
        var selector = "#" + canvasId;
        var target = new WgpuRenderTarget(device, queue, default,
            readback || NativeCanvasUsesRgba() != 0 ? WGPUTextureFormat.RGBA8Unorm : WGPUTextureFormat.BGRA8Unorm,
            WGPUCompositeAlphaMode.Opaque, WGPUPresentMode.Fifo) {
            _selector = Encoding.UTF8.GetBytes(selector + "\0"),
            _readbackCanvas = readback,
        };
        try {
            target.AttachCanvas(thread);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!target.IsCanvasReady()) {
                cancellationToken.ThrowIfCancellationRequested();
                target.CheckCanvasError();
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                    throw new TimeoutException("Canvas binding timed out.");
                await NextBrowserTurn();
                await Task.Yield();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!readback) {
                target._surface = Wgpu.CreateCanvasSurface(instance, selector);
                target._ownsSurface = true;
            }
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private unsafe void AttachCanvas(nuint thread)
    {
        fixed (byte* selector = _selector) {
            _canvasThread = thread;
            _canvasToken = NativeObserveDevice(selector, _device.Handle);
            if (_canvasToken == 0) throw new InvalidOperationException("This canvas already has a render target.");
            if (NativeBindCanvas(selector, thread, _readbackCanvas ? 1 : 0, _canvasToken) != 0)
                throw new OutOfMemoryException("Could not allocate the canvas binding request.");
        }
    }

    private unsafe bool IsCanvasReady()
    {
        fixed (byte* selector = _selector) return NativeCanvasReady(selector) != 0;
    }

    private unsafe void CheckCanvasError()
    {
        if (_selector is null) return;
        nint error;
        fixed (byte* selector = _selector) error = NativeDeviceError(selector);
        if (error == 0) return;
        try { throw new WgpuException(Marshal.PtrToStringUTF8(error) ?? "Canvas GPU error."); }
        finally { NativeFree(error); }
    }

    private unsafe void ResizeCanvas(int width, int height)
    {
        if (_selector is null) return;
        fixed (byte* selector = _selector) {
            var result = NativeResizeCanvas(selector, width, height);
            if (result != 0) throw new InvalidOperationException($"Canvas resize failed: {result}.");
        }
    }

    private void ResizeReadback(int width, int height)
    {
        var stride = checked((checked(width * 4) + 255) & ~255);
        var bytes = checked((ulong)stride * (ulong)height);
        if (bytes > Wgpu.GetLimits(_device).MaxBufferSize || bytes > int.MaxValue)
            throw new InvalidOperationException("Canvas exceeds the GPU readback buffer limit.");
        var texture = default(WgpuHandle<WGPUTexture>);
        var buffer = default(WgpuHandle<WGPUBuffer>);
        try {
            texture = Wgpu.CreateTexture(_device, new WGPUTextureDescriptor {
                Dimension = WGPUTextureDimension._2D,
                Size = new WGPUExtent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
                Format = Format, MipLevelCount = 1, SampleCount = 1,
                Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc,
            });
            buffer = Wgpu.CreateBuffer(_device, new WGPUBufferDescriptor {
                Size = bytes, Usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead,
            });
        }
        catch {
            DestroyReadback(ref buffer, ref texture);
            throw;
        }
        try { DestroyReadback(ref _readback, ref _outputTexture); }
        catch { DestroyReadback(ref buffer, ref texture); throw; }
        _outputTexture = texture;
        _readback = buffer;
        _bytesPerRow = stride;
    }

    private unsafe void BeginReadback()
    {
        var encoder = Wgpu.CreateCommandEncoder(_device);
        var commands = default(WgpuHandle<WGPUCommandBuffer>);
        try {
            Wgpu.CopyTextureToBuffer(encoder, _outputTexture, _readback,
                (uint)Width, (uint)Height, (uint)_bytesPerRow);
            commands = Wgpu.FinishCommandEncoder(encoder, new WGPUCommandBufferDescriptor());
            Wgpu.Submit(_queue, [commands]);
        }
        finally { Wgpu.Release(ref commands); Wgpu.Release(ref encoder); }
        fixed (byte* selector = _selector) NativeReadPixels(selector, _readback.Handle, Width, Height, _bytesPerRow);
    }

    private unsafe int PixelsPending()
    {
        fixed (byte* selector = _selector) return NativePixelsPending(selector);
    }

    private static void DestroyReadback(ref WgpuHandle<WGPUBuffer> buffer, ref WgpuHandle<WGPUTexture> texture)
    {
        try {
            try { if (!buffer.IsNull) Wgpu.DestroyBuffer(buffer); }
            finally { if (!texture.IsNull) Wgpu.DestroyTexture(texture); }
        }
        finally { Wgpu.Release(ref buffer); Wgpu.Release(ref texture); }
    }

    private unsafe void DisposeCanvas()
    {
        try {
            if (_selector is not null && _canvasToken != 0) {
                fixed (byte* selector = _selector) {
                    try {
                        if (NativeDetachCanvas(selector, _canvasThread, _canvasToken) != 0)
                            throw new OutOfMemoryException("Could not allocate the canvas release request.");
                    }
                    finally { NativeReleaseCanvas(selector, _canvasToken); }
                }
            }
        }
        finally {
            try { DestroyReadback(ref _readback, ref _outputTexture); }
            finally { if (_ownsSurface) Wgpu.Release(ref _surface); }
        }
    }

    [DllImport("__Internal_emscripten", EntryPoint = "emscripten_set_canvas_element_size")]
    private static extern unsafe int NativeResizeCanvas(byte* selector, int width, int height);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsObserveDevice")]
    private static extern unsafe int NativeObserveDevice(byte* selector, nint device);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsBindCanvas")]
    private static extern unsafe int NativeBindCanvas(byte* selector, nuint thread, int readback, int token);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsDetachCanvas")]
    private static extern unsafe int NativeDetachCanvas(byte* selector, nuint thread, int token);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsCanvasReady")]
    private static extern unsafe int NativeCanvasReady(byte* selector);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsCanvasUsesRgba")]
    private static extern int NativeCanvasUsesRgba();
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsDeviceError")]
    private static extern unsafe nint NativeDeviceError(byte* selector);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsFree")]
    private static extern void NativeFree(nint pointer);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsReadPixels")]
    private static extern unsafe void NativeReadPixels(byte* selector, nint buffer, int width, int height, int stride);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsPixelsPending")]
    private static extern unsafe int NativePixelsPending(byte* selector);
    [DllImport("__Internal_emscripten", EntryPoint = "siaGraphicsReleaseCanvas")]
    private static extern unsafe void NativeReleaseCanvas(byte* selector, int token);
}

public enum WgpuCanvasPresentation
{
    Direct,
    Readback,
}
#endif

namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{

    public static WgpuSurfaceCapabilities GetSurfaceCapabilities(
        WgpuHandle<WGPUSurface> surface,
        WgpuHandle<WGPUAdapter> adapter)
    {
        var surfacePointer = GetPointer(surface);
        var adapterPointer = GetPointer(adapter);
        var capabilities = WGPUSurfaceCapabilities.Default;
        try {
            if (WgpuUnsafe.wgpuSurfaceGetCapabilities(
                surfacePointer, adapterPointer, &capabilities) != WGPUStatus.Success) {
                throw new WgpuException("WebGPU failed to report the surface capabilities.");
            }
#if BROWSER
            if (capabilities.Usages == WGPUTextureUsage.None) {
                capabilities.Usages = WGPUTextureUsage.CopySrc | WGPUTextureUsage.CopyDst
                    | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.RenderAttachment;
            }
#endif
            return new(capabilities.Usages,
                CopyNativeArray(capabilities.Formats, capabilities.FormatCount),
                CopyNativeArray(capabilities.PresentModes, capabilities.PresentModeCount),
                CopyNativeArray(capabilities.AlphaModes, capabilities.AlphaModeCount));
        }
        finally {
            WgpuUnsafe.wgpuSurfaceCapabilitiesFreeMembers(capabilities);
        }
    }
    public static void ConfigureSurface(
        WgpuHandle<WGPUSurface> surface,
        in WGPUSurfaceConfiguration configuration)
    {
        fixed (WGPUSurfaceConfiguration* configurationPtr = &configuration) {
            WgpuUnsafe.wgpuSurfaceConfigure(GetPointer(surface), configurationPtr);
        }
    }

    public static WgpuSurfaceTexture AcquireSurfaceTexture(
        WgpuHandle<WGPUSurface> surface)
    {
        var native = new WGPUSurfaceTexture {
            NextInChain = null,
            Texture = null,
            Status = default,
        };

        WgpuUnsafe.wgpuSurfaceGetCurrentTexture(GetPointer(surface), &native);
        return new WgpuSurfaceTexture(
            native.Status,
            WgpuHandle<WGPUTexture>.FromOptionalPointer(native.Texture));
    }

    public static WGPUStatus PresentSurface(WgpuHandle<WGPUSurface> surface)
    {
#if BROWSER
        _ = GetPointer(surface);
        return WGPUStatus.Success;
#else
        return WgpuUnsafe.wgpuSurfacePresent(GetPointer(surface));
#endif
    }

    public static void PresentSurfaceOrThrow(WgpuHandle<WGPUSurface> surface)
    {
        var status = PresentSurface(surface);
        if (status != WGPUStatus.Success) {
            throw new WgpuException(
                $"Surface present failed with status {status}.");
        }
    }

    public static void UnconfigureSurface(WgpuHandle<WGPUSurface> surface) =>
        WgpuUnsafe.wgpuSurfaceUnconfigure(GetPointer(surface));

    public static void Release(ref WgpuSurfaceTexture surfaceTexture)
    {
        var texture = surfaceTexture.Texture;
        surfaceTexture = default;
        Release(ref texture);
    }
}

public readonly record struct WgpuSurfaceTexture(
    WGPUSurfaceGetCurrentTextureStatus Status,
    WgpuHandle<WGPUTexture> Texture)
{
    public bool HasTexture => !Texture.IsNull;
}

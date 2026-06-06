namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
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

    public static WGPUStatus PresentSurface(WgpuHandle<WGPUSurface> surface) =>
        WgpuUnsafe.wgpuSurfacePresent(GetPointer(surface));

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

/// <summary>A value snapshot returned by one surface acquisition.</summary>
public readonly record struct WgpuSurfaceTexture(
    WGPUSurfaceGetCurrentTextureStatus Status,
    WgpuHandle<WGPUTexture> Texture)
{
    public bool HasTexture => !Texture.IsNull;
}

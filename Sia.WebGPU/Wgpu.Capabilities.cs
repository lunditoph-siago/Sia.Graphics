namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static bool HasFeature(WgpuHandle<WGPUAdapter> adapter, WGPUFeatureName feature) =>
        WgpuUnsafe.wgpuAdapterHasFeature(GetPointer(adapter), feature) != 0;

    public static bool HasFeature(WgpuHandle<WGPUDevice> device, WGPUFeatureName feature) =>
        WgpuUnsafe.wgpuDeviceHasFeature(GetPointer(device), feature) != 0;

    public static WGPUFeatureName[] GetFeatures(WgpuHandle<WGPUAdapter> adapter)
    {
        var pointer = GetPointer(adapter);
        var features = WGPUSupportedFeatures.Default;
        try {
            WgpuUnsafe.wgpuAdapterGetFeatures(pointer, &features);
            return CopyNativeArray(features.Features, features.FeatureCount);
        }
        finally {
            WgpuUnsafe.wgpuSupportedFeaturesFreeMembers(features);
        }
    }

    public static WGPUFeatureName[] GetFeatures(WgpuHandle<WGPUDevice> device)
    {
        var pointer = GetPointer(device);
        var features = WGPUSupportedFeatures.Default;
        try {
            WgpuUnsafe.wgpuDeviceGetFeatures(pointer, &features);
            return CopyNativeArray(features.Features, features.FeatureCount);
        }
        finally {
            WgpuUnsafe.wgpuSupportedFeaturesFreeMembers(features);
        }
    }

    public static WGPULimits GetLimits(WgpuHandle<WGPUAdapter> adapter)
    {
        var limits = WGPULimits.Default;
        if (WgpuUnsafe.wgpuAdapterGetLimits(GetPointer(adapter), &limits) != WGPUStatus.Success) {
            throw new WgpuException("WebGPU failed to report the adapter limits.");
        }
        return limits;
    }

    public static WgpuAdapterInfo GetAdapterInfo(WgpuHandle<WGPUAdapter> adapter)
    {
        var pointer = GetPointer(adapter);
        var info = WGPUAdapterInfo.Default;
        try {
            if (WgpuUnsafe.wgpuAdapterGetInfo(pointer, &info) != WGPUStatus.Success) {
                throw new WgpuException("WebGPU failed to report the adapter information.");
            }
            return new(
                WgpuStringViewText.ToString(info.Vendor),
                WgpuStringViewText.ToString(info.Architecture),
                WgpuStringViewText.ToString(info.Device),
                WgpuStringViewText.ToString(info.Description),
                info.BackendType, info.AdapterType, info.VendorID, info.DeviceID,
                info.SubgroupMinSize, info.SubgroupMaxSize);
        }
        finally {
            WgpuUnsafe.wgpuAdapterInfoFreeMembers(info);
        }
    }

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

    private static T[] CopyNativeArray<T>(T* pointer, nuint count)
        where T : unmanaged
    {
        var length = checked((int)count);
        if (pointer == null && length != 0) {
            throw new WgpuException("WebGPU returned a null array with a nonzero length.");
        }
        return new ReadOnlySpan<T>(pointer, length).ToArray();
    }
}

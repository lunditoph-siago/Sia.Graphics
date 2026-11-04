namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static WgpuHandle<WGPUTexture> CreateTexture(
        WgpuHandle<WGPUDevice> device,
        in WGPUTextureDescriptor descriptor)
    {
        fixed (WGPUTextureDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUTexture>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateTexture(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUTextureView> CreateTextureView(
        WgpuHandle<WGPUTexture> texture,
        in WGPUTextureViewDescriptor descriptor)
    {
        fixed (WGPUTextureViewDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUTextureView>.FromPointer(
                WgpuUnsafe.wgpuTextureCreateView(GetPointer(texture), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUTextureView> CreateTextureView(
        WgpuSurfaceTexture surfaceTexture,
        in WGPUTextureViewDescriptor descriptor)
    {
        if (!surfaceTexture.HasTexture) {
            throw new ArgumentException(
                "The surface frame has no texture.", nameof(surfaceTexture));
        }

        return CreateTextureView(surfaceTexture.Texture, in descriptor);
    }

    public static WgpuHandle<WGPUSampler> CreateSampler(
        WgpuHandle<WGPUDevice> device,
        in WGPUSamplerDescriptor descriptor)
    {
        fixed (WGPUSamplerDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUSampler>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateSampler(GetPointer(device), descriptorPtr));
        }
    }

    public static void WriteTexture<T>(
        WgpuHandle<WGPUQueue> queue,
        WgpuHandle<WGPUTexture> texture,
        ReadOnlySpan<T> data,
        in WGPUTexelCopyBufferLayout layout,
        in WGPUExtent3D size,
        uint mipLevel = 0,
        WGPUOrigin3D origin = default,
        WGPUTextureAspect aspect = WGPUTextureAspect.All)
        where T : unmanaged
    {
        var destination = new WGPUTexelCopyTextureInfo {
            Texture = GetPointer(texture),
            MipLevel = mipLevel,
            Origin = origin,
            Aspect = aspect,
        };
        WriteTexture(queue, in destination, data, in layout, in size);
    }
    public static void WriteTexture<T>(
        WgpuHandle<WGPUQueue> queue,
        in WGPUTexelCopyTextureInfo destination,
        ReadOnlySpan<T> data,
        in WGPUTexelCopyBufferLayout layout,
        in WGPUExtent3D size)
        where T : unmanaged
    {
        var byteSize = checked((nuint)data.Length * (nuint)sizeof(T));
        fixed (T* dataPointer = data)
        fixed (WGPUTexelCopyTextureInfo* destinationPointer = &destination)
        fixed (WGPUTexelCopyBufferLayout* layoutPointer = &layout)
        fixed (WGPUExtent3D* sizePointer = &size) {
            WgpuUnsafe.wgpuQueueWriteTexture(
                GetPointer(queue), destinationPointer, dataPointer, byteSize, layoutPointer, sizePointer);
        }
    }

    public static void DestroyTexture(WgpuHandle<WGPUTexture> texture) =>
        WgpuUnsafe.wgpuTextureDestroy(GetPointer(texture));

    public static WgpuTextureInfo GetTextureInfo(
        WgpuHandle<WGPUTexture> texture) =>
        new(
            new WGPUExtent3D {
                Width = WgpuUnsafe.wgpuTextureGetWidth(GetPointer(texture)),
                Height = WgpuUnsafe.wgpuTextureGetHeight(GetPointer(texture)),
                DepthOrArrayLayers = WgpuUnsafe.wgpuTextureGetDepthOrArrayLayers(
                    GetPointer(texture)),
            },
            WgpuUnsafe.wgpuTextureGetDimension(GetPointer(texture)),
            WgpuUnsafe.wgpuTextureGetFormat(GetPointer(texture)),
            WgpuUnsafe.wgpuTextureGetUsage(GetPointer(texture)),
            WgpuUnsafe.wgpuTextureGetMipLevelCount(GetPointer(texture)),
            WgpuUnsafe.wgpuTextureGetSampleCount(GetPointer(texture)));
}

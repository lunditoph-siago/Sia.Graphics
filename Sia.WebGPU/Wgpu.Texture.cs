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

    public static void DestroyTexture(WgpuHandle<WGPUTexture> texture) =>
        WgpuUnsafe.wgpuTextureDestroy(GetPointer(texture));
}

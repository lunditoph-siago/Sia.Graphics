namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    /// <summary>Moves a canonical handle and clears its source.</summary>
    public static WgpuHandle<T> Move<T>(ref WgpuHandle<T> source)
        where T : unmanaged
    {
        var result = source;
        source = default;
        return result;
    }

    /// <summary>Releases one WebGPU reference and clears the canonical handle.</summary>
    public static void Release<T>(ref WgpuHandle<T> handle)
        where T : unmanaged
    {
        if (handle.IsNull) {
            return;
        }

        var pointer = handle.Handle;
        handle = default;

        if (typeof(T) == typeof(WGPUAdapter)) {
            WgpuUnsafe.wgpuAdapterRelease((WGPUAdapter*)pointer);
        }
        else if (typeof(T) == typeof(WGPUBindGroup)) {
            WgpuUnsafe.wgpuBindGroupRelease((WGPUBindGroup*)pointer);
        }
        else if (typeof(T) == typeof(WGPUBindGroupLayout)) {
            WgpuUnsafe.wgpuBindGroupLayoutRelease((WGPUBindGroupLayout*)pointer);
        }
        else if (typeof(T) == typeof(WGPUBuffer)) {
            WgpuUnsafe.wgpuBufferRelease((WGPUBuffer*)pointer);
        }
        else if (typeof(T) == typeof(WGPUCommandBuffer)) {
            WgpuUnsafe.wgpuCommandBufferRelease((WGPUCommandBuffer*)pointer);
        }
        else if (typeof(T) == typeof(WGPUCommandEncoder)) {
            WgpuUnsafe.wgpuCommandEncoderRelease((WGPUCommandEncoder*)pointer);
        }
        else if (typeof(T) == typeof(WGPUComputePassEncoder)) {
            WgpuUnsafe.wgpuComputePassEncoderRelease((WGPUComputePassEncoder*)pointer);
        }
        else if (typeof(T) == typeof(WGPUComputePipeline)) {
            WgpuUnsafe.wgpuComputePipelineRelease((WGPUComputePipeline*)pointer);
        }
        else if (typeof(T) == typeof(WGPUDevice)) {
            WgpuUnsafe.wgpuDeviceRelease((WGPUDevice*)pointer);
        }
        else if (typeof(T) == typeof(WGPUExternalTexture)) {
            WgpuUnsafe.wgpuExternalTextureRelease((WGPUExternalTexture*)pointer);
        }
        else if (typeof(T) == typeof(WGPUInstance)) {
            WgpuUnsafe.wgpuInstanceRelease((WGPUInstance*)pointer);
        }
        else if (typeof(T) == typeof(WGPUPipelineLayout)) {
            WgpuUnsafe.wgpuPipelineLayoutRelease((WGPUPipelineLayout*)pointer);
        }
        else if (typeof(T) == typeof(WGPUQuerySet)) {
            WgpuUnsafe.wgpuQuerySetRelease((WGPUQuerySet*)pointer);
        }
        else if (typeof(T) == typeof(WGPUQueue)) {
            WgpuUnsafe.wgpuQueueRelease((WGPUQueue*)pointer);
        }
        else if (typeof(T) == typeof(WGPURenderBundle)) {
            WgpuUnsafe.wgpuRenderBundleRelease((WGPURenderBundle*)pointer);
        }
        else if (typeof(T) == typeof(WGPURenderBundleEncoder)) {
            WgpuUnsafe.wgpuRenderBundleEncoderRelease((WGPURenderBundleEncoder*)pointer);
        }
        else if (typeof(T) == typeof(WGPURenderPassEncoder)) {
            WgpuUnsafe.wgpuRenderPassEncoderRelease((WGPURenderPassEncoder*)pointer);
        }
        else if (typeof(T) == typeof(WGPURenderPipeline)) {
            WgpuUnsafe.wgpuRenderPipelineRelease((WGPURenderPipeline*)pointer);
        }
        else if (typeof(T) == typeof(WGPUSampler)) {
            WgpuUnsafe.wgpuSamplerRelease((WGPUSampler*)pointer);
        }
        else if (typeof(T) == typeof(WGPUShaderModule)) {
            WgpuUnsafe.wgpuShaderModuleRelease((WGPUShaderModule*)pointer);
        }
        else if (typeof(T) == typeof(WGPUSurface)) {
            WgpuUnsafe.wgpuSurfaceRelease((WGPUSurface*)pointer);
        }
        else if (typeof(T) == typeof(WGPUTexture)) {
            WgpuUnsafe.wgpuTextureRelease((WGPUTexture*)pointer);
        }
        else if (typeof(T) == typeof(WGPUTextureView)) {
            WgpuUnsafe.wgpuTextureViewRelease((WGPUTextureView*)pointer);
        }
        else {
            handle = new WgpuHandle<T>(pointer);
            throw new NotSupportedException(
                $"No WebGPU release operation is known for {typeof(T).Name}.");
        }
    }
}

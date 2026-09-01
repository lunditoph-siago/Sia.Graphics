using Sia.WebGPU;

namespace Sia.Graphics.Compatibility;

public sealed record GpuTargetProfile(
    bool SupportsVertexStageStorageBuffers,
    uint MaxStorageBuffersPerShaderStage,
    ulong MaxStorageBufferBindingSize,
    uint MaxUniformBuffersPerShaderStage,
    ulong MaxUniformBufferBindingSize,
    uint MaxVertexBuffers,
    uint MaxVertexAttributes,
    ulong MaxVertexBufferArrayStride,
    ulong MaxBufferSize)
{
    public static GpuTargetProfile Query(WgpuHandle<WGPUDevice> device)
    {
        var limits = Wgpu.GetLimits(device);
        return new GpuTargetProfile(
            SupportsReliableVertexStorageBuffers(),
            limits.MaxStorageBuffersPerShaderStage,
            limits.MaxStorageBufferBindingSize,
            limits.MaxUniformBuffersPerShaderStage,
            limits.MaxUniformBufferBindingSize,
            limits.MaxVertexBuffers,
            limits.MaxVertexAttributes,
            limits.MaxVertexBufferArrayStride,
            limits.MaxBufferSize);
    }

    private static bool SupportsReliableVertexStorageBuffers()
    {
#if BROWSER && SIA_WEBGPU_BACKEND_WGPU
        return false;
#else
        return true;
#endif
    }
}

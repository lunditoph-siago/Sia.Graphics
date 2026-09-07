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
    public uint MaxStorageBuffersInVertexStage { get; init; } =
        SupportsVertexStageStorageBuffers ? MaxStorageBuffersPerShaderStage : 0;

    public uint MaxStorageBuffersInFragmentStage { get; init; } = MaxStorageBuffersPerShaderStage;

    public uint GetStorageBufferLimit(WGPUShaderStage stage) => stage switch {
        WGPUShaderStage.Vertex => SupportsVertexStageStorageBuffers
            ? System.Math.Min(MaxStorageBuffersPerShaderStage, MaxStorageBuffersInVertexStage) : 0,
        WGPUShaderStage.Fragment => System.Math.Min(MaxStorageBuffersPerShaderStage, MaxStorageBuffersInFragmentStage),
        WGPUShaderStage.Compute => MaxStorageBuffersPerShaderStage,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    public static GpuTargetProfile Query(WgpuHandle<WGPUDevice> device)
    {
        var limits = Wgpu.GetLimits(device, out var stageLimits);
        var vertexLimit = stageLimits.MaxStorageBuffersInVertexStage != uint.MaxValue
            ? stageLimits.MaxStorageBuffersInVertexStage
            : SupportsReliableVertexStorageBuffers() ? limits.MaxStorageBuffersPerShaderStage : 0;
        return new GpuTargetProfile(
            vertexLimit > 0,
            limits.MaxStorageBuffersPerShaderStage,
            limits.MaxStorageBufferBindingSize,
            limits.MaxUniformBuffersPerShaderStage,
            limits.MaxUniformBufferBindingSize,
            limits.MaxVertexBuffers,
            limits.MaxVertexAttributes,
            limits.MaxVertexBufferArrayStride,
            limits.MaxBufferSize) {
            MaxStorageBuffersInVertexStage = vertexLimit,
            MaxStorageBuffersInFragmentStage = stageLimits.MaxStorageBuffersInFragmentStage != uint.MaxValue
                ? stageLimits.MaxStorageBuffersInFragmentStage : limits.MaxStorageBuffersPerShaderStage
        };
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

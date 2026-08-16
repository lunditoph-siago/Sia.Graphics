namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static WgpuHandle<WGPUQuerySet> CreateQuerySet(
        WgpuHandle<WGPUDevice> device,
        in WGPUQuerySetDescriptor descriptor)
    {
        fixed (WGPUQuerySetDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUQuerySet>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateQuerySet(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUQuerySet> CreateQuerySet(
        WgpuHandle<WGPUDevice> device,
        WGPUQueryType type,
        uint count,
        string? label = null)
    {
        using var labelString = WgpuOwnedString.Create(label);
        var descriptor = new WGPUQuerySetDescriptor {
            NextInChain = null,
            Label = labelString.View,
            Type = type,
            Count = count,
        };
        return CreateQuerySet(device, in descriptor);
    }

    public static void WriteTimestamp(
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        WgpuHandle<WGPUQuerySet> querySet,
        uint queryIndex) =>
        WgpuUnsafe.wgpuCommandEncoderWriteTimestamp(
            GetPointer(commandEncoder), GetPointer(querySet), queryIndex);

    public static void ResolveQuerySet(
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        WgpuHandle<WGPUQuerySet> querySet,
        uint firstQuery,
        uint queryCount,
        WgpuHandle<WGPUBuffer> destination,
        ulong destinationOffset = 0) =>
        WgpuUnsafe.wgpuCommandEncoderResolveQuerySet(
            GetPointer(commandEncoder),
            GetPointer(querySet),
            firstQuery,
            queryCount,
            GetPointer(destination),
            destinationOffset);
}

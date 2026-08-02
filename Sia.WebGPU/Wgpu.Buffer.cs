namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static WgpuHandle<WGPUBuffer> CreateBuffer(
        WgpuHandle<WGPUDevice> device,
        in WGPUBufferDescriptor descriptor)
    {
        fixed (WGPUBufferDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUBuffer>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateBuffer(GetPointer(device), descriptorPtr));
        }
    }

    public static ulong GetBufferSize(WgpuHandle<WGPUBuffer> buffer) =>
        WgpuUnsafe.wgpuBufferGetSize(GetPointer(buffer));

    public static WGPUBufferUsage GetBufferUsage(WgpuHandle<WGPUBuffer> buffer) =>
        WgpuUnsafe.wgpuBufferGetUsage(GetPointer(buffer));

    public static WgpuBufferInfo GetBufferInfo(WgpuHandle<WGPUBuffer> buffer) =>
        new(GetBufferSize(buffer), GetBufferUsage(buffer));

    public static WGPUBufferMapState GetBufferMapState(WgpuHandle<WGPUBuffer> buffer) =>
        WgpuUnsafe.wgpuBufferGetMapState(GetPointer(buffer));

    public static void DestroyBuffer(WgpuHandle<WGPUBuffer> buffer) =>
        WgpuUnsafe.wgpuBufferDestroy(GetPointer(buffer));

    public static void UnmapBuffer(WgpuHandle<WGPUBuffer> buffer) =>
        WgpuUnsafe.wgpuBufferUnmap(GetPointer(buffer));

    public static void WriteBuffer<T>(
        WgpuHandle<WGPUQueue> queue,
        WgpuHandle<WGPUBuffer> buffer,
        ulong bufferOffset,
        ReadOnlySpan<T> data)
        where T : unmanaged
    {
        if (data.IsEmpty) {
            return;
        }

        var byteLength = checked((nuint)data.Length * (nuint)sizeof(T));
        fixed (T* dataPtr = data) {
            WgpuUnsafe.wgpuQueueWriteBuffer(
                GetPointer(queue),
                GetPointer(buffer),
                bufferOffset,
                dataPtr,
                byteLength);
        }
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    public static Task MapBufferReadAsync(
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset,
        ulong size,
        CancellationToken cancellationToken = default)
    {
        var state = AsyncRequestState<bool>.Create(cancellationToken);

        try {
            var callbackInfo = new WGPUBufferMapCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<
                    WGPUMapAsyncStatus,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)&OnBufferMap,
                Userdata1 = state.UserData,
                Userdata2 = null,
            };

            WgpuUnsafe.wgpuBufferMapAsync(
                GetPointer(buffer), WGPUMapMode.Read, (nuint)offset, (nuint)size, callbackInfo);
            return state.Task;
        }
        catch {
            state.Dispose();
            throw;
        }
    }

    public static void MapBufferRead(
        WgpuHandle<WGPUInstance> instance,
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset,
        ulong size,
        TimeSpan? timeout = null)
    {
        var task = MapBufferReadAsync(buffer, offset, size);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!task.IsCompleted) {
            ProcessEvents(instance);
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException(
                    "Timed out waiting for the WebGPU buffer map callback.");
            }
        }
        task.GetAwaiter().GetResult();
    }

    public static ReadOnlySpan<T> GetMappedRangeReadOnly<T>(
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset,
        int count)
        where T : unmanaged
    {
        var byteSize = checked((nuint)count * (nuint)sizeof(T));
        var pointer = WgpuUnsafe.wgpuBufferGetConstMappedRange(GetPointer(buffer), (nuint)offset, byteSize);
        if (pointer == null) {
            throw new InvalidOperationException(
                "WebGPU could not get the mapped buffer range.");
        }
        return new ReadOnlySpan<T>(pointer, count);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnBufferMap(
        WGPUMapAsyncStatus status,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<bool>.FromUserData(userdata1);
        try {
            if (status == WGPUMapAsyncStatus.Success) {
                state.TrySetResult(true);
            }
            else {
                state.TrySetException(CreateRequestException("MapBufferRead", status.ToString(), message));
            }
        }
        finally {
            state.Dispose();
        }
    }
}

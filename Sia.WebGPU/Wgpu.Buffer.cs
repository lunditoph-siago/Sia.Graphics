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

    public static WGPUBufferMapState GetBufferMapState(WgpuHandle<WGPUBuffer> buffer)
    {
#if BROWSER
        return WgpuUnsafe.wgpuBufferGetMapState(GetPointer(buffer));
#else
        _ = GetPointer(buffer);
        throw new PlatformNotSupportedException(
            "wgpu-native 29 does not implement buffer map state queries. Track mapping through MapBufferReadAsync and UnmapBuffer.");
#endif
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        WgpuUnsafe.wgpuBufferAddRef(GetPointer(buffer));
        try {
            return AwaitBufferMapAsync(BeginBufferMap(buffer, offset, size), buffer, cancellationToken);
        }
        catch {
            Release(ref buffer);
            throw;
        }
    }

    private static Task<bool> BeginBufferMap(
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset,
        ulong size)
    {
        var nativeOffset = checked((nuint)offset);
        if (size == ulong.MaxValue) {
            var bufferSize = GetBufferSize(buffer);
            if (offset > bufferSize) throw new ArgumentOutOfRangeException(nameof(offset));
            size = bufferSize - offset;
        }
        var nativeSize = checked((nuint)size);
        var state = AsyncRequestState<bool>.Create();

        try {
            var callbackInfo = new WGPUBufferMapCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<
                    WGPUMapAsyncStatus,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)(delegate* unmanaged[Cdecl]<WGPUMapAsyncStatus,
                    WgpuCallbackMessage, void*, void*, void>)&OnBufferMap,
                Userdata1 = state.UserData,
                Userdata2 = null,
            };

            WgpuUnsafe.wgpuBufferMapAsync(
                GetPointer(buffer), WGPUMapMode.Read, nativeOffset, nativeSize, callbackInfo);
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
        if (OperatingSystem.IsBrowser()) {
            throw new PlatformNotSupportedException("Use MapBufferReadAsync in the browser.");
        }
        _ = GetPointer(instance);
        var duration = timeout ?? TimeSpan.FromSeconds(5);
        if (duration < TimeSpan.Zero && duration != Timeout.InfiniteTimeSpan) {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        var task = BeginBufferMap(buffer, offset, size);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        while (!task.IsCompleted) {
            ProcessEvents(instance);
            if (task.IsCompleted) break;
            if (duration != Timeout.InfiniteTimeSpan
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) >= duration) {
                UnmapBuffer(buffer);
                _ = task.ContinueWith(static failed => _ = failed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new TimeoutException(
                    "Timed out waiting for the WebGPU buffer map callback.");
            }
            spinner.SpinOnce();
        }
        task.GetAwaiter().GetResult();
    }

    public static ReadOnlySpan<T> GetMappedRangeReadOnly<T>(
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset,
        int count)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var byteSize = checked((nuint)count * (nuint)sizeof(T));
        var pointer = WgpuUnsafe.wgpuBufferGetConstMappedRange(GetPointer(buffer), checked((nuint)offset), byteSize);
        if (pointer == null) {
            throw new InvalidOperationException(
                "WebGPU could not get the mapped buffer range.");
        }
        return new ReadOnlySpan<T>(pointer, count);
    }

    public static void ReadMappedRange<T>(
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset,
        Span<T> destination)
        where T : unmanaged
    {
        var pointer = GetPointer(buffer);
        var nativeOffset = checked((nuint)offset);
        var byteSize = checked((nuint)destination.Length * (nuint)sizeof(T));
#if BROWSER
        fixed (T* data = destination) {
            if (WgpuUnsafe.wgpuBufferReadMappedRange(pointer, nativeOffset, data, byteSize) != WGPUStatus.Success) {
                throw new WgpuException("WebGPU could not read the mapped buffer range.");
            }
        }
#else
        var mapped = WgpuUnsafe.wgpuBufferGetConstMappedRange(pointer, nativeOffset, byteSize);
        if (mapped == null) {
            throw new WgpuException("WebGPU could not read the mapped buffer range.");
        }
        new ReadOnlySpan<T>(mapped, destination.Length).CopyTo(destination);
#endif
    }

    public static void WriteMappedRange<T>(
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset,
        ReadOnlySpan<T> source)
        where T : unmanaged
    {
        var pointer = GetPointer(buffer);
        var nativeOffset = checked((nuint)offset);
        var byteSize = checked((nuint)source.Length * (nuint)sizeof(T));
#if BROWSER
        fixed (T* data = source) {
            if (WgpuUnsafe.wgpuBufferWriteMappedRange(pointer, nativeOffset, data, byteSize) != WGPUStatus.Success) {
                throw new WgpuException("WebGPU could not write the mapped buffer range.");
            }
        }
#else
        var mapped = WgpuUnsafe.wgpuBufferGetMappedRange(pointer, nativeOffset, byteSize);
        if (mapped == null) {
            throw new WgpuException("WebGPU could not write the mapped buffer range.");
        }
        source.CopyTo(new Span<T>(mapped, source.Length));
#endif
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnBufferMap(
        WGPUMapAsyncStatus status,
        WgpuCallbackMessage message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<bool>.FromUserData(userdata1);
        try {
            if (status == WGPUMapAsyncStatus.Success) {
                state.TrySetResult(true);
            }
            else {
                state.TrySetException(CreateRequestException("MapBufferRead", status.ToString(), ReadCallbackMessage(message)));
            }
        }
        finally {
            state.Dispose();
        }
    }
}

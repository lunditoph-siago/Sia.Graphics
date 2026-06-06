using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Sia;

namespace Sia.WebGPU;

/// <summary>
/// Bridges native asynchronous requests into Sia components and main-thread events.
/// </summary>
public sealed unsafe class WgpuRequests : IAddon
{
    private sealed class PendingRequest(
        WgpuRequests owner,
        Entity target,
        WgpuRequestKind kind) : IDisposable
    {
        private GCHandle _self;

        public WgpuRequests Owner { get; } = owner;
        public Entity Target { get; } = target;
        public WgpuRequestKind Kind { get; } = kind;

        public void* UserData => (void*)GCHandle.ToIntPtr(_self);

        public static PendingRequest Create(
            WgpuRequests owner,
            Entity target,
            WgpuRequestKind kind)
        {
            var request = new PendingRequest(owner, target, kind);
            request._self = GCHandle.Alloc(request);
            return request;
        }

        public static PendingRequest FromUserData(void* userData) =>
            (PendingRequest)GCHandle.FromIntPtr((nint)userData).Target!;

        public void Dispose()
        {
            if (_self.IsAllocated) {
                _self.Free();
            }
        }
    }

    private readonly record struct Completion(
        Entity Target,
        WgpuRequestKind Kind,
        bool Success,
        nint Handle,
        string Status,
        string Message);

    private readonly ConcurrentQueue<Completion> _completions = new();
    private volatile bool _accepting;

    public void OnInitialize(World world)
    {
        world.AcquireAddon<WgpuResources>();
        _accepting = true;
    }

    public void OnUninitialize(World world)
    {
        _accepting = false;
        while (_completions.TryDequeue(out var completion)) {
            Release(in completion);
        }
    }

    public WGPUFuture RequestAdapter(
        Entity target,
        WgpuHandle<WGPUInstance> instance,
        in WGPURequestAdapterOptions options)
    {
        ValidateTarget(target);
        var request = PendingRequest.Create(
            this, target, WgpuRequestKind.Adapter);

        try {
            var callback = new WGPURequestAdapterCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (void*)(delegate* unmanaged[Cdecl]<
                    WGPURequestAdapterStatus,
                    WGPUAdapter*,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)&OnAdapterCompleted,
                Userdata1 = request.UserData,
                Userdata2 = null,
            };

            fixed (WGPURequestAdapterOptions* optionsPointer = &options) {
                return WgpuUnsafe.wgpuInstanceRequestAdapter(
                    instance.Pointer, optionsPointer, callback);
            }
        }
        catch {
            request.Dispose();
            throw;
        }
    }

    public WGPUFuture RequestDevice(
        Entity target,
        WgpuHandle<WGPUAdapter> adapter,
        in WGPUDeviceDescriptor descriptor)
    {
        ValidateTarget(target);
        var request = PendingRequest.Create(
            this, target, WgpuRequestKind.Device);

        try {
            var callback = new WGPURequestDeviceCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (void*)(delegate* unmanaged[Cdecl]<
                    WGPURequestDeviceStatus,
                    WGPUDevice*,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)&OnDeviceCompleted,
                Userdata1 = request.UserData,
                Userdata2 = null,
            };

            fixed (WGPUDeviceDescriptor* descriptorPointer = &descriptor) {
                return WgpuUnsafe.wgpuAdapterRequestDevice(
                    adapter.Pointer, descriptorPointer, callback);
            }
        }
        catch {
            request.Dispose();
            throw;
        }
    }

    internal void Submit(World world)
    {
        while (_completions.TryDequeue(out var completion)) {
            if (!completion.Target.IsValid) {
                Release(in completion);
                continue;
            }

            if (!completion.Success) {
                Release(in completion);
                world.Send(
                    completion.Target,
                    new WgpuRequestEvents.Failed(
                        completion.Kind,
                        completion.Status,
                        completion.Message));
                continue;
            }

            if (completion.Kind == WgpuRequestKind.Adapter) {
                try {
                    world.AttachWgpu(
                        completion.Target,
                        new WgpuHandle<WGPUAdapter>(completion.Handle));
                }
                catch (Exception exception) {
                    world.Send(
                        completion.Target,
                        new WgpuRequestEvents.Failed(
                            completion.Kind,
                            "PublishFailed",
                            exception.Message));
                    continue;
                }

                world.Send(completion.Target, new WgpuRequestEvents.AdapterReady());
            } else {
                try {
                    var device = new WgpuHandle<WGPUDevice>(completion.Handle);
                    world.AttachWgpu(completion.Target, device);

                    try {
                        world.AttachWgpu(completion.Target, Wgpu.GetQueue(device));
                    }
                    catch {
                        completion.Target.ReleaseWgpu<WGPUDevice>();
                        throw;
                    }

                }
                catch (Exception exception) {
                    world.Send(
                        completion.Target,
                        new WgpuRequestEvents.Failed(
                            completion.Kind,
                            "PublishFailed",
                            exception.Message));
                    continue;
                }

                world.Send(completion.Target, new WgpuRequestEvents.DeviceReady());
            }
        }
    }

    private void ValidateTarget(Entity target)
    {
        if (!_accepting) {
            throw new ObjectDisposedException(nameof(WgpuRequests));
        }
        if (!target.IsValid) {
            throw new ObjectDisposedException(nameof(target));
        }
    }

    private void Complete(in Completion completion)
    {
        // Enqueue first, then re-check: if OnUninitialize turned _accepting
        // off and drained concurrently, this drain catches the stragglers.
        _completions.Enqueue(completion);
        if (!_accepting) {
            while (_completions.TryDequeue(out var pending)) {
                Release(in pending);
            }
        }
    }

    private static void Release(in Completion completion)
    {
        if (completion.Handle == 0) {
            return;
        }

        if (completion.Kind == WgpuRequestKind.Adapter) {
            var adapter = new WgpuHandle<WGPUAdapter>(completion.Handle);
            Wgpu.Release(ref adapter);
        } else {
            var device = new WgpuHandle<WGPUDevice>(completion.Handle);
            Wgpu.Release(ref device);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAdapterCompleted(
        WGPURequestAdapterStatus status,
        WGPUAdapter* adapter,
        WGPUStringView message,
        void* userData1,
        void* userData2)
    {
        PendingRequest? request = null;
        try {
            request = PendingRequest.FromUserData(userData1);
            request.Owner.Complete(new Completion(
                request.Target,
                request.Kind,
                status == WGPURequestAdapterStatus.Success && adapter != null,
                (nint)adapter,
                status.ToString(),
                ReadMessage(message)));
        }
        catch {
            if (adapter != null) {
                try {
                    var handle = new WgpuHandle<WGPUAdapter>((nint)adapter);
                    Wgpu.Release(ref handle);
                }
                catch {
                    // Exceptions cannot cross an unmanaged callback boundary.
                }
            }
        } finally {
            request?.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceCompleted(
        WGPURequestDeviceStatus status,
        WGPUDevice* device,
        WGPUStringView message,
        void* userData1,
        void* userData2)
    {
        PendingRequest? request = null;
        try {
            request = PendingRequest.FromUserData(userData1);
            request.Owner.Complete(new Completion(
                request.Target,
                request.Kind,
                status == WGPURequestDeviceStatus.Success && device != null,
                (nint)device,
                status.ToString(),
                ReadMessage(message)));
        }
        catch {
            if (device != null) {
                try {
                    var handle = new WgpuHandle<WGPUDevice>((nint)device);
                    Wgpu.Release(ref handle);
                }
                catch {
                    // Exceptions cannot cross an unmanaged callback boundary.
                }
            }
        } finally {
            request?.Dispose();
        }
    }

    private static string ReadMessage(WGPUStringView message)
    {
        try {
            return WgpuStringViewText.ToString(message);
        }
        catch {
            return "The native callback returned an invalid UTF-8 message.";
        }
    }
}

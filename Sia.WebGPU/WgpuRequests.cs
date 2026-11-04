using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Sia;

namespace Sia.WebGPU;

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
    private World? _world;
    private TaskScheduler _callbackScheduler = TaskScheduler.Default;
    private volatile bool _accepting;

    public void OnInitialize(World world)
    {
        world.AcquireAddon<WgpuResources>();
        _world = world;
        _callbackScheduler = SynchronizationContext.Current is null
            ? TaskScheduler.Default
            : TaskScheduler.FromCurrentSynchronizationContext();
        _accepting = true;
    }

    public void OnUninitialize(World world)
    {
        _accepting = false;
        _world = null;
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
        if (instance.IsNull) throw new ArgumentException("The instance is null.", nameof(instance));
        var request = PendingRequest.Create(
            this, target, WgpuRequestKind.Adapter);

        try {
            var callback = new WGPURequestAdapterCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<
                    WGPURequestAdapterStatus,
                    WGPUAdapter*,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)(delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus,
                    WGPUAdapter*, WgpuCallbackMessage, void*, void*, void>)&OnAdapterCompleted,
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
        if (adapter.IsNull) throw new ArgumentException("The adapter is null.", nameof(adapter));
        var request = PendingRequest.Create(
            this, target, WgpuRequestKind.Device);

        try {
            var callback = new WGPURequestDeviceCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<
                    WGPURequestDeviceStatus,
                    WGPUDevice*,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)(delegate* unmanaged[Cdecl]<WGPURequestDeviceStatus,
                    WGPUDevice*, WgpuCallbackMessage, void*, void*, void>)&OnDeviceCompleted,
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
                var adapter = new WgpuHandle<WGPUAdapter>(completion.Handle);
                try {
                    world.AttachWgpu(completion.Target, ref adapter);
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
                finally {
                    Wgpu.Release(ref adapter);
                }

                world.Send(completion.Target, new WgpuRequestEvents.AdapterReady());
            }
            else {
                var device = new WgpuHandle<WGPUDevice>(completion.Handle);
                var ownedDevice = device;
                try {
                    world.AttachWgpu(completion.Target, ref ownedDevice);

                    try {
                        var queue = Wgpu.GetQueue(device);
                        try {
                            world.AttachWgpu(completion.Target, ref queue);
                        }
                        finally {
                            Wgpu.Release(ref queue);
                        }
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
                finally {
                    Wgpu.Release(ref ownedDevice);
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
        WgpuWorldExtensions.ValidateWgpuTarget(_world!, target);
    }

    private void Complete(in Completion completion)
    {
        _completions.Enqueue(completion);
        if (!_accepting) {
            while (_completions.TryDequeue(out var pending)) {
                ReleaseAfterCallback(in pending);
            }
        }
    }

    private void ReleaseAfterCallback(in Completion completion)
    {
        var pending = completion;
        _ = Task.Factory.StartNew(() => Release(in pending),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, _callbackScheduler);
    }

    private static void Release(in Completion completion)
    {
        if (completion.Handle == 0) {
            return;
        }

        if (completion.Kind == WgpuRequestKind.Adapter) {
            var adapter = new WgpuHandle<WGPUAdapter>(completion.Handle);
            Wgpu.Release(ref adapter);
        }
        else {
            var device = new WgpuHandle<WGPUDevice>(completion.Handle);
            Wgpu.Release(ref device);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAdapterCompleted(
        WGPURequestAdapterStatus status,
        WGPUAdapter* adapter,
        WgpuCallbackMessage message,
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
                    request!.Owner.ReleaseAfterCallback(new Completion(
                        default, WgpuRequestKind.Adapter, false, (nint)adapter, "", ""));
                }
                catch {
                }
            }
        }
        finally {
            request?.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceCompleted(
        WGPURequestDeviceStatus status,
        WGPUDevice* device,
        WgpuCallbackMessage message,
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
                    request!.Owner.ReleaseAfterCallback(new Completion(
                        default, WgpuRequestKind.Device, false, (nint)device, "", ""));
                }
                catch {
                }
            }
        }
        finally {
            request?.Dispose();
        }
    }

    private static string ReadMessage(WgpuCallbackMessage message)
    {
        try {
            return WgpuStringViewText.ToString(Wgpu.ReadCallbackMessage(message));
        }
        catch {
            return "The native callback returned an invalid UTF-8 message.";
        }
    }
}

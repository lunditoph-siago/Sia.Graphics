using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static WgpuHandle<WGPUAdapter> RequestAdapter(
        WgpuHandle<WGPUInstance> instance,
        CancellationToken cancellationToken = default)
    {
        var options = new WGPURequestAdapterOptions {
            NextInChain = null,
            FeatureLevel = WGPUFeatureLevel.Undefined,
            PowerPreference = WGPUPowerPreference.Undefined,
            ForceFallbackAdapter = 0,
            BackendType = WGPUBackendType.Undefined,
            CompatibleSurface = null,
        };

        return RequestAdapter(instance, in options, cancellationToken);
    }

    public static WgpuHandle<WGPUAdapter> RequestAdapter(
        WgpuHandle<WGPUInstance> instance,
        in WGPURequestAdapterOptions options,
        CancellationToken cancellationToken = default) =>
        RequestAdapterAsync(instance, in options, cancellationToken).GetAwaiter().GetResult();

    public static Task<WgpuHandle<WGPUAdapter>> RequestAdapterAsync(
        WgpuHandle<WGPUInstance> instance,
        CancellationToken cancellationToken = default)
    {
        var options = new WGPURequestAdapterOptions {
            NextInChain = null,
            FeatureLevel = WGPUFeatureLevel.Undefined,
            PowerPreference = WGPUPowerPreference.Undefined,
            ForceFallbackAdapter = 0,
            BackendType = WGPUBackendType.Undefined,
            CompatibleSurface = null,
        };

        return RequestAdapterAsync(instance, in options, cancellationToken);
    }

    public static Task<WgpuHandle<WGPUAdapter>> RequestAdapterAsync(
        WgpuHandle<WGPUInstance> instance,
        in WGPURequestAdapterOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = AsyncRequestState<WgpuHandle<WGPUAdapter>>.Create();

        try {
            var callbackInfo = new WGPURequestAdapterCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<
                    WGPURequestAdapterStatus,
                    WGPUAdapter*,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)(delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus,
                    WGPUAdapter*, WgpuCallbackMessage, void*, void*, void>)&OnRequestAdapter,
                Userdata1 = state.UserData,
                Userdata2 = null,
            };

            fixed (WGPURequestAdapterOptions* optionsPtr = &options) {
                WgpuUnsafe.wgpuInstanceRequestAdapter(GetPointer(instance), optionsPtr, callbackInfo);
            }

            return AwaitHandleAsync(state.Task, cancellationToken);
        }
        catch {
            state.Dispose();
            throw;
        }
    }

    public static Task<WgpuHandle<WGPUDevice>> RequestDeviceAsync(
        WgpuHandle<WGPUAdapter> adapter,
        CancellationToken cancellationToken = default)
    {
        var descriptor = new WGPUDeviceDescriptor {
            NextInChain = null,
            Label = default,
            RequiredFeatureCount = 0,
            RequiredFeatures = null,
            RequiredLimits = null,
            DefaultQueue = new WGPUQueueDescriptor {
                NextInChain = null,
                Label = default,
            },
            DeviceLostCallbackInfo = default,
            UncapturedErrorCallbackInfo = default,
        };

        return RequestDeviceAsync(adapter, in descriptor, cancellationToken);
    }

    public static WgpuHandle<WGPUDevice> RequestDevice(
        WgpuHandle<WGPUAdapter> adapter,
        CancellationToken cancellationToken = default)
    {
        var descriptor = new WGPUDeviceDescriptor {
            NextInChain = null,
            Label = default,
            RequiredFeatureCount = 0,
            RequiredFeatures = null,
            RequiredLimits = null,
            DefaultQueue = new WGPUQueueDescriptor {
                NextInChain = null,
                Label = default,
            },
            DeviceLostCallbackInfo = default,
            UncapturedErrorCallbackInfo = default,
        };

        return RequestDevice(adapter, in descriptor, cancellationToken);
    }

    public static WgpuHandle<WGPUDevice> RequestDevice(
        WgpuHandle<WGPUAdapter> adapter,
        in WGPUDeviceDescriptor descriptor,
        CancellationToken cancellationToken = default) =>
        RequestDeviceAsync(adapter, in descriptor, cancellationToken).GetAwaiter().GetResult();

    public static WgpuHandle<WGPUDevice> RequestDevice(
        WgpuHandle<WGPUAdapter> adapter,
        ReadOnlySpan<WGPUFeatureName> requiredFeatures,
        CancellationToken cancellationToken = default) =>
        RequestDeviceAsync(adapter, requiredFeatures, cancellationToken).GetAwaiter().GetResult();

    public static Task<WgpuHandle<WGPUDevice>> RequestDeviceAsync(
        WgpuHandle<WGPUAdapter> adapter,
        ReadOnlySpan<WGPUFeatureName> requiredFeatures,
        CancellationToken cancellationToken = default)
    {
        fixed (WGPUFeatureName* requiredFeaturesPtr = requiredFeatures) {
            var descriptor = new WGPUDeviceDescriptor {
                NextInChain = null,
                Label = default,
                RequiredFeatureCount = (nuint)requiredFeatures.Length,
                RequiredFeatures = requiredFeaturesPtr,
                RequiredLimits = null,
                DefaultQueue = new WGPUQueueDescriptor {
                    NextInChain = null,
                    Label = default,
                },
                DeviceLostCallbackInfo = default,
                UncapturedErrorCallbackInfo = default,
            };

            return RequestDeviceAsync(adapter, in descriptor, cancellationToken);
        }
    }

    public static Task<WgpuHandle<WGPUDevice>> RequestDeviceAsync(
        WgpuHandle<WGPUAdapter> adapter,
        in WGPUDeviceDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = AsyncRequestState<WgpuHandle<WGPUDevice>>.Create();

        try {
            var callbackInfo = new WGPURequestDeviceCallbackInfo {
                NextInChain = null,
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<
                    WGPURequestDeviceStatus,
                    WGPUDevice*,
                    WGPUStringView,
                    void*,
                    void*,
                    void>)(delegate* unmanaged[Cdecl]<WGPURequestDeviceStatus,
                    WGPUDevice*, WgpuCallbackMessage, void*, void*, void>)&OnRequestDevice,
                Userdata1 = state.UserData,
                Userdata2 = null,
            };

            fixed (WGPUDeviceDescriptor* descriptorPtr = &descriptor) {
                WgpuUnsafe.wgpuAdapterRequestDevice(GetPointer(adapter), descriptorPtr, callbackInfo);
            }

            return AwaitHandleAsync(state.Task, cancellationToken);
        }
        catch {
            state.Dispose();
            throw;
        }
    }

    public static WgpuHandle<WGPUQueue> GetQueue(WgpuHandle<WGPUDevice> device) =>
        WgpuHandle<WGPUQueue>.FromPointer(
            WgpuUnsafe.wgpuDeviceGetQueue(GetPointer(device)));

    public static Task OnSubmittedWorkDoneAsync(
        WgpuHandle<WGPUQueue> queue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = AsyncRequestState<bool>.Create();
        try {
            var callback = new WGPUQueueWorkDoneCallbackInfo {
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus,
                    WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus,
                    WgpuCallbackMessage, void*, void*, void>)&OnSubmittedWorkDone,
                Userdata1 = state.UserData,
            };
            WgpuUnsafe.wgpuQueueOnSubmittedWorkDone(GetPointer(queue), callback);
            return state.Task.WaitAsync(cancellationToken);
        }
        catch {
            state.Dispose();
            throw;
        }
    }

    public static WGPULimits GetLimits(WgpuHandle<WGPUDevice> device)
    {
        var limits = WGPULimits.Default;
        var status = WgpuUnsafe.wgpuDeviceGetLimits(GetPointer(device), &limits);
        if (status != WGPUStatus.Success) {
            throw new InvalidOperationException("WebGPU failed to report the device limits.");
        }
        return limits;
    }

    public static WGPULimits GetLimits(
        WgpuHandle<WGPUDevice> device, out WGPUCompatibilityModeLimits compatibilityLimits)
    {
        var extension = WGPUCompatibilityModeLimits.Default;
#if BROWSER
        extension.Chain.SType = WGPUSType.CompatibilityModeLimits;
        var limits = WGPULimits.Default;
        limits.NextInChain = &extension.Chain;
        var status = WgpuUnsafe.wgpuDeviceGetLimits(GetPointer(device), &limits);
        if (status != WGPUStatus.Success) {
            throw new InvalidOperationException("WebGPU failed to report the device stage limits.");
        }
        limits.NextInChain = null;
#else
        var limits = GetLimits(device);
#endif
        compatibilityLimits = extension;
        return limits;
    }

    public static bool HasFeature(WgpuHandle<WGPUAdapter> adapter, WGPUFeatureName feature) =>
        WgpuUnsafe.wgpuAdapterHasFeature(GetPointer(adapter), feature) != 0;
    public static bool HasFeature(WgpuHandle<WGPUDevice> device, WGPUFeatureName feature) =>
        WgpuUnsafe.wgpuDeviceHasFeature(GetPointer(device), feature) != 0;
    public static WGPUFeatureName[] GetFeatures(WgpuHandle<WGPUAdapter> adapter)
    {
        var pointer = GetPointer(adapter);
        var features = WGPUSupportedFeatures.Default;
        try {
            WgpuUnsafe.wgpuAdapterGetFeatures(pointer, &features);
            return CopyNativeArray(features.Features, features.FeatureCount);
        }
        finally {
            WgpuUnsafe.wgpuSupportedFeaturesFreeMembers(features);
        }
    }
    public static WGPUFeatureName[] GetFeatures(WgpuHandle<WGPUDevice> device)
    {
        var pointer = GetPointer(device);
        var features = WGPUSupportedFeatures.Default;
        try {
            WgpuUnsafe.wgpuDeviceGetFeatures(pointer, &features);
            return CopyNativeArray(features.Features, features.FeatureCount);
        }
        finally {
            WgpuUnsafe.wgpuSupportedFeaturesFreeMembers(features);
        }
    }
    public static WGPULimits GetLimits(WgpuHandle<WGPUAdapter> adapter)
    {
        var limits = WGPULimits.Default;
        if (WgpuUnsafe.wgpuAdapterGetLimits(GetPointer(adapter), &limits) != WGPUStatus.Success) {
            throw new WgpuException("WebGPU failed to report the adapter limits.");
        }
        return limits;
    }
    public static WgpuAdapterInfo GetAdapterInfo(WgpuHandle<WGPUAdapter> adapter)
    {
        var pointer = GetPointer(adapter);
        var info = WGPUAdapterInfo.Default;
        try {
            if (WgpuUnsafe.wgpuAdapterGetInfo(pointer, &info) != WGPUStatus.Success) {
                throw new WgpuException("WebGPU failed to report the adapter information.");
            }
            return new(
                WgpuStringViewText.ToString(info.Vendor),
                WgpuStringViewText.ToString(info.Architecture),
                WgpuStringViewText.ToString(info.Device),
                WgpuStringViewText.ToString(info.Description),
                info.BackendType, info.AdapterType, info.VendorID, info.DeviceID,
                info.SubgroupMinSize, info.SubgroupMaxSize);
        }
        finally {
            WgpuUnsafe.wgpuAdapterInfoFreeMembers(info);
        }
    }

    public static void DestroyDevice(WgpuHandle<WGPUDevice> device) =>
        WgpuUnsafe.wgpuDeviceDestroy(GetPointer(device));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSubmittedWorkDone(
        WGPUQueueWorkDoneStatus status,
        WgpuCallbackMessage message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<bool>.FromUserData(userdata1);
        try {
            if (status == WGPUQueueWorkDoneStatus.Success) {
                state.TrySetResult(true);
            }
            else {
                state.TrySetException(CreateRequestException(
                    "OnSubmittedWorkDone", status.ToString(), ReadCallbackMessage(message)));
            }
        }
        finally {
            state.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRequestAdapter(
        WGPURequestAdapterStatus status,
        WGPUAdapter* adapter,
        WgpuCallbackMessage message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<WgpuHandle<WGPUAdapter>>.FromUserData(userdata1);
        try {
            if (status == WGPURequestAdapterStatus.Success && adapter != null) {
                var result = WgpuHandle<WGPUAdapter>.FromPointer(adapter);
                state.TrySetResult(result);
            }
            else {
                state.TrySetException(CreateRequestException("RequestAdapter", status.ToString(), ReadCallbackMessage(message)));
            }
        }
        finally {
            state.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRequestDevice(
        WGPURequestDeviceStatus status,
        WGPUDevice* device,
        WgpuCallbackMessage message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<WgpuHandle<WGPUDevice>>.FromUserData(userdata1);
        try {
            if (status == WGPURequestDeviceStatus.Success && device != null) {
                var result = WgpuHandle<WGPUDevice>.FromPointer(device);
                state.TrySetResult(result);
            }
            else {
                state.TrySetException(CreateRequestException("RequestDevice", status.ToString(), ReadCallbackMessage(message)));
            }
        }
        finally {
            state.Dispose();
        }
    }
}

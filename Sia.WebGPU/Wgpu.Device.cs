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
        var state = AsyncRequestState<WgpuHandle<WGPUAdapter>>.Create(cancellationToken);

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
                    void>)&OnRequestAdapter,
                Userdata1 = state.UserData,
                Userdata2 = null,
            };

            fixed (WGPURequestAdapterOptions* optionsPtr = &options) {
                WgpuUnsafe.wgpuInstanceRequestAdapter(GetPointer(instance), optionsPtr, callbackInfo);
            }

            return state.Task;
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
        var state = AsyncRequestState<WgpuHandle<WGPUDevice>>.Create(cancellationToken);

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
                    void>)&OnRequestDevice,
                Userdata1 = state.UserData,
                Userdata2 = null,
            };

            fixed (WGPUDeviceDescriptor* descriptorPtr = &descriptor) {
                WgpuUnsafe.wgpuAdapterRequestDevice(GetPointer(adapter), descriptorPtr, callbackInfo);
            }

            return state.Task;
        }
        catch {
            state.Dispose();
            throw;
        }
    }

    public static WgpuHandle<WGPUQueue> GetQueue(WgpuHandle<WGPUDevice> device) =>
        WgpuHandle<WGPUQueue>.FromPointer(
            WgpuUnsafe.wgpuDeviceGetQueue(GetPointer(device)));

    public static WGPULimits GetLimits(WgpuHandle<WGPUDevice> device)
    {
        var limits = WGPULimits.Default;
        var status = WgpuUnsafe.wgpuDeviceGetLimits(GetPointer(device), &limits);
        if (status != WGPUStatus.Success) {
            throw new InvalidOperationException("WebGPU failed to report the device limits.");
        }
        return limits;
    }

    public static void DestroyDevice(WgpuHandle<WGPUDevice> device) =>
        WgpuUnsafe.wgpuDeviceDestroy(GetPointer(device));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRequestAdapter(
        WGPURequestAdapterStatus status,
        WGPUAdapter* adapter,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<WgpuHandle<WGPUAdapter>>.FromUserData(userdata1);
        try {
            if (status == WGPURequestAdapterStatus.Success && adapter != null) {
                var result = WgpuHandle<WGPUAdapter>.FromPointer(adapter);
                if (!state.TrySetResult(result)) {
                    Release(ref result);
                }
            }
            else {
                state.TrySetException(CreateRequestException("RequestAdapter", status.ToString(), message));
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
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<WgpuHandle<WGPUDevice>>.FromUserData(userdata1);
        try {
            if (status == WGPURequestDeviceStatus.Success && device != null) {
                var result = WgpuHandle<WGPUDevice>.FromPointer(device);
                if (!state.TrySetResult(result)) {
                    Release(ref result);
                }
            }
            else {
                state.TrySetException(CreateRequestException("RequestDevice", status.ToString(), message));
            }
        }
        finally {
            state.Dispose();
        }
    }
}

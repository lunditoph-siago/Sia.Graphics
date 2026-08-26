using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    private static T* GetPointer<T>(WgpuHandle<T> handle)
        where T : unmanaged
    {
        return handle.IsNull
            ? throw new ArgumentException(
                $"The {typeof(T).Name} handle is null.", nameof(handle))
            : handle.Pointer;
    }

    public static WgpuHandle<WGPUInstance> CreateInstance()
    {
        var descriptor = new WGPUInstanceDescriptor {
            NextInChain = null,
            RequiredFeatureCount = 0,
            RequiredFeatures = null,
            RequiredLimits = null,
        };

        return CreateInstance(in descriptor);
    }

    public static WgpuHandle<WGPUInstance> CreateSpirvInstance()
    {
#if BROWSER
        return CreateInstance();
#else
        var feature = WGPUInstanceFeatureName.ShaderSourceSPIRV;
        var descriptor = new WGPUInstanceDescriptor {
            NextInChain = null,
            RequiredFeatureCount = 1,
            RequiredFeatures = &feature,
            RequiredLimits = null,
        };
        return CreateInstance(in descriptor);
#endif
    }

    public static WgpuHandle<WGPUInstance> CreateInstance(in WGPUInstanceDescriptor descriptor)
    {
        fixed (WGPUInstanceDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUInstance>.FromPointer(
                WgpuUnsafe.wgpuCreateInstance(descriptorPtr));
        }
    }

    public static void ProcessEvents(WgpuHandle<WGPUInstance> instance) =>
        WgpuUnsafe.wgpuInstanceProcessEvents(GetPointer(instance));

    public static WgpuHandle<WGPUSurface> CreateSurface(
        WgpuHandle<WGPUInstance> instance,
        in WGPUSurfaceDescriptor descriptor)
    {
        fixed (WGPUSurfaceDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUSurface>.FromPointer(
                WgpuUnsafe.wgpuInstanceCreateSurface(GetPointer(instance), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUSurface> CreateWindowsSurface(
        WgpuHandle<WGPUInstance> instance,
        nint hinstance,
        nint hwnd,
        string? label = null)
    {
        if (hinstance == 0) {
            throw new ArgumentException("HINSTANCE must not be zero.", nameof(hinstance));
        }
        if (hwnd == 0) {
            throw new ArgumentException("HWND must not be zero.", nameof(hwnd));
        }

        using var labelString = WgpuOwnedString.Create(label);
        var source = new WGPUSurfaceSourceWindowsHWND {
            Chain = new WGPUChainedStruct {
                Next = null,
                SType = WGPUSType.SurfaceSourceWindowsHWND,
            },
            Hinstance = (void*)hinstance,
            Hwnd = (void*)hwnd,
        };

        var descriptor = new WGPUSurfaceDescriptor {
            NextInChain = &source.Chain,
            Label = labelString.View,
        };

        return CreateSurface(instance, in descriptor);
    }

    public static WgpuHandle<WGPUSurface> CreateMetalLayerSurface(
        WgpuHandle<WGPUInstance> instance,
        nint layer,
        string? label = null)
    {
        if (layer == 0) {
            throw new ArgumentException("CAMetalLayer must not be zero.", nameof(layer));
        }

        using var labelString = WgpuOwnedString.Create(label);
        var source = new WGPUSurfaceSourceMetalLayer {
            Chain = new WGPUChainedStruct {
                Next = null,
                SType = WGPUSType.SurfaceSourceMetalLayer,
            },
            Layer = (void*)layer,
        };
        var descriptor = new WGPUSurfaceDescriptor {
            NextInChain = &source.Chain,
            Label = labelString.View,
        };

        return CreateSurface(instance, in descriptor);
    }

    public static WgpuHandle<WGPUSurface> CreateWaylandSurface(
        WgpuHandle<WGPUInstance> instance,
        nint display,
        nint surface,
        string? label = null)
    {
        if (display == 0) {
            throw new ArgumentException("Wayland display must not be zero.", nameof(display));
        }
        if (surface == 0) {
            throw new ArgumentException("Wayland surface must not be zero.", nameof(surface));
        }

        using var labelString = WgpuOwnedString.Create(label);
        var source = new WGPUSurfaceSourceWaylandSurface {
            Chain = new WGPUChainedStruct {
                Next = null,
                SType = WGPUSType.SurfaceSourceWaylandSurface,
            },
            Display = (void*)display,
            Surface = (void*)surface,
        };
        var descriptor = new WGPUSurfaceDescriptor {
            NextInChain = &source.Chain,
            Label = labelString.View,
        };

        return CreateSurface(instance, in descriptor);
    }

    public static WgpuHandle<WGPUSurface> CreateXlibSurface(
        WgpuHandle<WGPUInstance> instance,
        nint display,
        ulong window,
        string? label = null)
    {
        if (display == 0) {
            throw new ArgumentException("Xlib display must not be zero.", nameof(display));
        }
        if (window == 0) {
            throw new ArgumentException("Xlib window must not be zero.", nameof(window));
        }

        using var labelString = WgpuOwnedString.Create(label);
        var source = new WGPUSurfaceSourceXlibWindow {
            Chain = new WGPUChainedStruct {
                Next = null,
                SType = WGPUSType.SurfaceSourceXlibWindow,
            },
            Display = (void*)display,
            Window = window,
        };
        var descriptor = new WGPUSurfaceDescriptor {
            NextInChain = &source.Chain,
            Label = labelString.View,
        };

        return CreateSurface(instance, in descriptor);
    }

    public static WgpuHandle<WGPUSurface> CreateXcbSurface(
        WgpuHandle<WGPUInstance> instance,
        nint connection,
        uint window,
        string? label = null)
    {
        if (connection == 0) {
            throw new ArgumentException("XCB connection must not be zero.", nameof(connection));
        }
        if (window == 0) {
            throw new ArgumentException("XCB window must not be zero.", nameof(window));
        }

        using var labelString = WgpuOwnedString.Create(label);
        var source = new WGPUSurfaceSourceXCBWindow {
            Chain = new WGPUChainedStruct {
                Next = null,
                SType = WGPUSType.SurfaceSourceXCBWindow,
            },
            Connection = (void*)connection,
            Window = window,
        };
        var descriptor = new WGPUSurfaceDescriptor {
            NextInChain = &source.Chain,
            Label = labelString.View,
        };

        return CreateSurface(instance, in descriptor);
    }

    public static WgpuHandle<WGPUSurface> CreateCanvasSurface(
        WgpuHandle<WGPUInstance> instance,
        string selector,
        string? label = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);

        using var selectorString = WgpuOwnedString.Create(selector);
        using var labelString = WgpuOwnedString.Create(label);
        var source = new WGPUEmscriptenSurfaceSourceCanvasHTMLSelector {
            Chain = new WGPUChainedStruct {
                Next = null,
                SType = (WGPUSType)0x00040000,
            },
            Selector = selectorString.View,
        };
        var descriptor = new WGPUSurfaceDescriptor {
            NextInChain = &source.Chain,
            Label = labelString.View,
        };

        return CreateSurface(instance, in descriptor);
    }

    internal static WgpuException CreateRequestException(string operation, string status, WGPUStringView message)
    {
        var details = WgpuStringViewText.ToString(message);
        return string.IsNullOrWhiteSpace(details)
            ? new WgpuException($"{operation} failed with status {status}.")
            : new WgpuException($"{operation} failed with status {status}: {details}");
    }

    internal sealed class AsyncRequestState<T> : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private GCHandle _selfHandle;

        private AsyncRequestState(TaskCompletionSource<T> completion, CancellationToken cancellationToken)
        {
            Completion = completion;
            _cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((AsyncRequestState<T>)state!).TrySetCanceled(), this)
                : default;
        }

        public TaskCompletionSource<T> Completion { get; }

        public Task<T> Task => Completion.Task;

        public void* UserData => (void*)GCHandle.ToIntPtr(_selfHandle);

        public static AsyncRequestState<T> Create(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = new AsyncRequestState<T>(completion, cancellationToken);
            state._selfHandle = GCHandle.Alloc(state);
            return state;
        }

        public static AsyncRequestState<T> FromUserData(void* userdata)
        {
            var handle = GCHandle.FromIntPtr((nint)userdata);
            return (AsyncRequestState<T>)handle.Target!;
        }

        public bool TrySetResult(T value) => Completion.TrySetResult(value);

        public void TrySetException(Exception exception) => Completion.TrySetException(exception);

        private void TrySetCanceled() => Completion.TrySetCanceled();

        public void Dispose()
        {
            _cancellationRegistration.Dispose();
            if (_selfHandle.IsAllocated) {
                _selfHandle.Free();
            }
        }
    }
}

public sealed class WgpuException : Exception
{
    public WgpuException(string message)
        : base(message)
    {
    }
}

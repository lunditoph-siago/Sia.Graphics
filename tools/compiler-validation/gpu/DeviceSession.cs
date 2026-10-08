using System.Runtime.InteropServices;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

/// <summary>Selected modules can raise limits and union required features before device creation.</summary>
public unsafe sealed class DeviceConfiguration
{
    public WGPULimits Limits = WGPULimits.Default;
    public WGPUCompatibilityModeLimits CompatibilityLimits = WGPUCompatibilityModeLimits.Default;
    public HashSet<WGPUFeatureName> Features { get; } = [];

    public DeviceConfiguration()
    {
        Limits.MaxStorageBuffersPerShaderStage = 6;
        Limits.MaxBindGroups = 4;
        Limits.MaxComputeInvocationsPerWorkgroup = 64;
        Limits.MaxComputeWorkgroupSizeX = 64;
        CompatibilityLimits.MaxStorageBuffersInVertexStage = 3;
        CompatibilityLimits.MaxStorageBuffersInFragmentStage = 1;
    }
}
public sealed class DeviceSession : IDisposable
{
    public static System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();
    private WgpuHandle<WGPUInstance> _instance;
    private WgpuHandle<WGPUAdapter> _adapter;
    private WgpuHandle<WGPUDevice> _device;
    private WgpuHandle<WGPUQueue> _queue;


    public WgpuHandle<WGPUDevice> Device => _device;
    public WgpuHandle<WGPUQueue> Queue => _queue;

    public static async Task<DeviceSession> CreateAsync(Report report, DeviceConfiguration configuration)
    {
        var session = new DeviceSession();
        try {
            session._instance = Wgpu.CreateSpirvInstance();
            session._adapter = await session.WaitAsync(RequestAdapter(session._instance, report.RequestedFeatureLevel));
            session._device = await session.WaitAsync(RequestDevice(session._adapter, configuration));
            session._queue = Wgpu.GetQueue(session._device);
            session.Describe(report);
            return session;
        } catch { session.Dispose(); throw; }
    }

    private static unsafe Task<WgpuHandle<WGPUAdapter>> RequestAdapter(WgpuHandle<WGPUInstance> instance, string feature)
    {
        var options = WGPURequestAdapterOptions.Default;
        options.FeatureLevel = feature == "core" ? WGPUFeatureLevel.Core : WGPUFeatureLevel.Compatibility;
        return Wgpu.RequestAdapterAsync(instance, options);
    }

    private static unsafe Task<WgpuHandle<WGPUDevice>> RequestDevice(WgpuHandle<WGPUAdapter> adapter, DeviceConfiguration configuration)
    {
        var limits = configuration.Limits;
        limits.NextInChain = null;
#if BROWSER
        var stages = configuration.CompatibilityLimits;
        limits.NextInChain = &stages.Chain;
#endif
        var descriptor = WGPUDeviceDescriptor.Default;
        descriptor.RequiredLimits = &limits;
#if !BROWSER
        descriptor.UncapturedErrorCallbackInfo.Callback = &NativeError;
#endif
        var features = configuration.Features.Order().ToArray();
        fixed (WGPUFeatureName* names = features)
        {
            descriptor.RequiredFeatures = names;
            descriptor.RequiredFeatureCount = (nuint)features.Length;
            return Wgpu.RequestDeviceAsync(adapter, descriptor);
        }
    }

#if !BROWSER
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void NativeError(WGPUDevice** device, WGPUErrorType type, WGPUStringView message, void* u1, void* u2) => RecordError(type, message);
#endif
    private static unsafe void RecordError(WGPUErrorType type, WGPUStringView message)
    {
        if (Errors.Count >= 32) return;
        var error = $"{type}: " + (message.Data == null ? "" : Marshal.PtrToStringUTF8((nint)message.Data, (int)System.Math.Min(message.Length, 4096)));
        Errors.Enqueue(error);
#if !BROWSER
        Console.Error.WriteLine(error);
#endif
    }

    private unsafe void Describe(Report report)
    {
        report.CoreFeature = WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)_device.DangerousGetHandle(), WGPUFeatureName.CoreFeaturesAndLimits) != 0;
        var limits = Wgpu.GetLimits(_device);
        report.Limits.Add("MaxStorageBuffersPerShaderStage", limits.MaxStorageBuffersPerShaderStage);
        report.Limits.Add("MaxBindGroups", limits.MaxBindGroups);
        report.Limits.Add("MaxComputeInvocationsPerWorkgroup", limits.MaxComputeInvocationsPerWorkgroup);
        var info = WGPUAdapterInfo.Default;
        if (WgpuUnsafe.wgpuAdapterGetInfo((WGPUAdapter*)_adapter.DangerousGetHandle(), &info) != WGPUStatus.Success) { report.Device = "adapter info unavailable"; return; }
        try {
            static string Text(WGPUStringView text) => text.Data == null ? "" : Marshal.PtrToStringUTF8((nint)text.Data, checked((int)text.Length)) ?? "";
            report.Device = $"{Text(info.Vendor)}; {Text(info.Architecture)}; {Text(info.Device)}; {info.BackendType}";
        } finally { WgpuUnsafe.wgpuAdapterInfoFreeMembers(info); }
    }

    public async Task<T> WaitAsync<T>(Task<T> task)
    {
        var deadline = Environment.TickCount64 + 30000;
        while (!task.IsCompleted) {
            Wgpu.ProcessEvents(_instance);
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("GPU callback timeout (30 seconds)");
            await Task.Delay(1);
        }
        return await task;
    }

    public async Task WaitAsync(Task task)
    {
        await WaitAsync(Complete(task));
        static async Task<bool> Complete(Task task) { await task; return true; }
    }

    public void Dispose()
    {

        if (!_queue.IsNull) Wgpu.Release(ref _queue);
        if (!_device.IsNull) Wgpu.Release(ref _device);
        if (!_adapter.IsNull) Wgpu.Release(ref _adapter);
        if (!_instance.IsNull) Wgpu.Release(ref _instance);
    }
}

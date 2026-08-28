using Sia;

namespace Sia.WebGPU;

public static class WgpuRequestExtensions
{
    public static WGPUFuture RequestWgpuAdapter(
        this World world,
        Entity target,
        in WGPURequestAdapterOptions options)
    {
        var requests = world.AcquireAddon<WgpuRequests>();
        return requests.RequestAdapter(
            target,
            target.GetWgpu<WGPUInstance>(),
            in options);
    }

    public static WGPUFuture RequestWgpuDevice(
        this World world,
        Entity target,
        in WGPUDeviceDescriptor descriptor)
    {
        var requests = world.AcquireAddon<WgpuRequests>();
        return requests.RequestDevice(
            target,
            target.GetWgpu<WGPUAdapter>(),
            in descriptor);
    }
}

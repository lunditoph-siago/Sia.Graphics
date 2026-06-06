using Sia;

namespace Sia.WebGPU;

public static class WgpuRequestExtensions
{
    /// <summary>
    /// Requests an adapter for the instance resource on <paramref name="target"/>.
    /// Add <see cref="WgpuRequestSystem"/> to publish the result component and event.
    /// </summary>
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

    /// <summary>
    /// Requests a device for the adapter resource on <paramref name="target"/>.
    /// Completion attaches device and queue components to the same entity.
    /// </summary>
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

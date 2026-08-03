using Sia;
using Sia.Reactive;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public static class WgpuRenderGraphWorldExtensions
{
    public static WgpuRenderGraphRegistry ConfigureWgpuRenderGraph(
        this World world,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue)
    {
        ArgumentNullException.ThrowIfNull(world);
        var registry = world.AcquireAddon<WgpuRenderGraphRegistry>();
        registry.Configure(device, queue);
        world.AcquireAddon<Reconciler>();
        return registry;
    }

    public static WgpuRenderGraphPlan PrepareWgpuRenderGraph(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.FlushReactive();
        return world.AcquireAddon<WgpuRenderGraphRegistry>().PreparePlan();
    }

    public static void ExecuteWgpuRenderGraph(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.FlushReactive();
        world.AcquireAddon<WgpuRenderGraphRegistry>().Execute();
    }

    public static SystemChain AddWgpuRenderGraph(this SystemChain chain) =>
        chain.Add<WgpuReactiveRenderGraphSystem>();
}

using Sia;

namespace Sia.WebGPU;

public sealed class WgpuProcessEventsSystem()
    : SystemBase(Matchers.Of<WgpuResource<WGPUInstance>>())
{
    public override void Initialize(World world)
    {
        world.AcquireAddon<WgpuResources>();
    }

    public override void Execute(WorldContext context, IEntityQuery query)
    {
        query.ForSlice(static (ref WgpuResource<WGPUInstance> resource) =>
            Wgpu.ProcessEvents(resource.Handle));
    }
}

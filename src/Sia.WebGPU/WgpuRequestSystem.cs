using Sia;

namespace Sia.WebGPU;

/// <summary>Publishes native request completions on the Sia thread.</summary>
public sealed class WgpuRequestSystem : SystemBase
{
    private WgpuRequests? _requests;

    public override void Initialize(World world)
    {
        _requests = world.AcquireAddon<WgpuRequests>();
    }

    public override void Uninitialize(World world)
    {
        _requests = null;
    }

    public override void Execute(World world, IEntityQuery query)
    {
        _requests?.Submit(world);
    }
}

using Sia;

namespace Sia.Graphics.Reactive;

public sealed class WgpuReactiveRenderGraphSystem : SystemBase
{
    public override void Execute(WorldContext context, IEntityQuery query)
    {
        if (!context.World.TryGetAddon<WgpuRenderGraphRegistry>(out var registry)) {
            throw new InvalidOperationException(
                "Configure the reactive render graph before scheduling its execution system.");
        }
        registry.Execute();
    }
}

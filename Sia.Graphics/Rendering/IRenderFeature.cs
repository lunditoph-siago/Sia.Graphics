using Sia.Graphics.Reactive;
using Sia.Reactive;

namespace Sia.Graphics.Rendering;

public interface IRenderFeature<TContext>
{
    public RenderFeatureKey Key { get; }

    public void Configure(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        in TContext context);
}

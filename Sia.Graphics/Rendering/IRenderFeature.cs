using Sia.Graphics.Reactive;
using Sia.Reactive;

namespace Sia.Graphics.Rendering;

public interface IRenderFeature<TContext>
{
    RenderFeatureKey Key { get; }

    void Configure(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        in TContext context);
}

using Sia.Graphics.Reactive;
using Sia.Reactive;

namespace Sia.Graphics.Rendering;

public sealed class DelegateRenderFeature<TContext>(
    RenderFeatureKey key,
    RenderFeatureConfigurator<TContext> configure) : IRenderFeature<TContext>
{
    public RenderFeatureKey Key { get; } = key;

    public void Configure(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        in TContext context) =>
        configure(ref hooks, registry, in context);
}

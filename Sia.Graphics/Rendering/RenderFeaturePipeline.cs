using Sia.Graphics.Reactive;
using Sia.Reactive;

namespace Sia.Graphics.Rendering;

public sealed class RenderFeaturePipeline<TContext>
{
    private readonly IRenderFeature<TContext>[] _features;

    public IReadOnlyList<IRenderFeature<TContext>> Features => _features;

    internal RenderFeaturePipeline(IRenderFeature<TContext>[] features)
    {
        _features = features;
    }

    public void Configure(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        in TContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var feature in _features) {
            feature.Configure(ref hooks, registry, in context);
        }
    }
}

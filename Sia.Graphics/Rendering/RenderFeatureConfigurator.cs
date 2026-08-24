using Sia.Graphics.Reactive;
using Sia.Reactive;

namespace Sia.Graphics.Rendering;

public delegate void RenderFeatureConfigurator<TContext>(
    ref Hooks hooks,
    WgpuRenderGraphRegistry registry,
    in TContext context);

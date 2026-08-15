using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.WebGPU;
using SiaReactive = Sia.Reactive.Reactive;

namespace Sia.Graphics.UI;

public readonly record struct UiRenderPassProps(
    WgpuRenderGraphRegistry Registry,
    World World,
    UiRenderer Renderer,
    RenderGraphPassKey Pass,
    RenderGraphTextureKey Output,
    Size Viewport,
    WGPULoadOp LoadOp = WGPULoadOp.Load,
    bool OutputCacheable = true);

public static class ReactiveUi
{
    public static ReactiveNode RenderPass(in UiRenderPassProps props, ref Hooks hooks)
    {
        hooks.UseUiRenderPass(
            props.Registry,
            props.World,
            props.Renderer,
            props.Pass,
            props.Output,
            props.Viewport,
            props.LoadOp,
            props.OutputCacheable);
        return SiaReactive.None;
    }

    public static ReactiveMount<UiRenderPassProps> MountRenderPass(
        this World world,
        in UiRenderPassProps props) =>
        world.Mount(RenderPass, props);
}

using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public static class UiRenderGraphHooks
{
    public static void UseUiRenderPass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        World world,
        UiRenderer renderer,
        RenderGraphPassKey pass,
        RenderGraphTextureKey output,
        Size viewport,
        WGPULoadOp loadOp = WGPULoadOp.Load)
    {
        hooks.UseRenderGraphPass(
            registry, pass, "ui",
            declaration => declaration.Write(output, RenderGraphTextureUsage.RenderAttachment));

        hooks.UseWgpuRenderGraphPassHandler(
            registry, pass,
            context => renderer.Render(world, context, output, viewport, loadOp));
    }
}

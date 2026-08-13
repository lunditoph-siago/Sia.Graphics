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
        var state = hooks.UseRef(() => new UiRenderPassState(output));
        if (state.Value.Output != output)
            state.Value = new(output);
        state.Value.Update(world, renderer, viewport, loadOp);

        hooks.UseRenderGraphPass(
            registry, pass, "ui",
            state.Value.Declare);

        hooks.UseWgpuRenderGraphPassHandler(
            registry, pass,
            state.Value.Render);
    }

    private sealed class UiRenderPassState(RenderGraphTextureKey output)
    {
        private World? _world;
        private UiRenderer? _renderer;
        private Size _viewport;
        private WGPULoadOp _loadOp;

        public RenderGraphTextureKey Output { get; } = output;

        public void Update(
            World world,
            UiRenderer renderer,
            Size viewport,
            WGPULoadOp loadOp)
        {
            _world = world;
            _renderer = renderer;
            _viewport = viewport;
            _loadOp = loadOp;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(Output, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context) =>
            _renderer!.Render(_world!, context, Output, _viewport, _loadOp);
    }
}

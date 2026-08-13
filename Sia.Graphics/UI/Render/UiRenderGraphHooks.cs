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
        var declaration = hooks.UseRef(() => new UiPassDeclaration(output));
        if (declaration.Value.Output != output)
            declaration.Value = new(output);

        var state = hooks.UseRef(static () => new UiRenderPassState());
        state.Value.Update(world, renderer, output, viewport, loadOp);

        hooks.UseRenderGraphPass(
            registry, pass, "ui",
            declaration.Value.Declare);

        hooks.UseWgpuRenderGraphPassHandler(
            registry, pass,
            state.Value.Render);
    }

    private sealed class UiPassDeclaration(RenderGraphTextureKey output)
    {
        public RenderGraphTextureKey Output { get; } = output;

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(Output, RenderGraphTextureUsage.RenderAttachment);
    }

    private sealed class UiRenderPassState
    {
        private World? _world;
        private UiRenderer? _renderer;
        private RenderGraphTextureKey _output;
        private Size _viewport;
        private WGPULoadOp _loadOp;

        public void Update(
            World world,
            UiRenderer renderer,
            RenderGraphTextureKey output,
            Size viewport,
            WGPULoadOp loadOp)
        {
            _world = world;
            _renderer = renderer;
            _output = output;
            _viewport = viewport;
            _loadOp = loadOp;
        }

        public void Render(WgpuReactiveRenderGraphPassContext context) =>
            _renderer!.Render(_world!, context, _output, _viewport, _loadOp);
    }
}

using Sia;
using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public static class SceneRenderGraphHooks
{
    public static void UseDepthPrepass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        SceneRenderer renderer,
        in GpuFrame frame,
        Entity cameraEntity,
        RenderGraphPassKey pass,
        RenderGraphTextureKey depth)
    {
        var state = hooks.UseRef(() => new DepthPrepassState(depth));
        if (state.Value.Depth != depth) {
            state.Value = new(depth);
        }
        state.Value.Update(renderer, in frame, cameraEntity);

        hooks.UseRenderGraphPass(registry, pass, "scene-depth-prepass", state.Value.Declare);
        hooks.UseWgpuRenderGraphPassHandler(registry, pass, state.Value.Render);
    }

    public static void UseForwardOpaquePass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        SceneRenderer renderer,
        in GpuFrame frame,
        RenderGraphPassKey pass,
        RenderGraphTextureKey color,
        RenderGraphTextureKey depth,
        WGPULoadOp colorLoadOp = WGPULoadOp.Clear,
        bool colorCacheable = true)
    {
        var state = hooks.UseRef(() => new ForwardOpaqueState(color, depth));
        if (state.Value.Color != color || state.Value.Depth != depth) {
            state.Value = new(color, depth);
        }
        state.Value.Update(renderer, in frame, colorLoadOp, colorCacheable);

        hooks.UseRenderGraphPass(registry, pass, "scene-forward-opaque", state.Value.Declare);
        hooks.UseWgpuRenderGraphPassHandler(registry, pass, state.Value.Render);
    }

    private sealed class DepthPrepassState(RenderGraphTextureKey depth)
    {
        private SceneRenderer? _renderer;
        private GpuFrame _frame;
        private Entity _camera;

        public RenderGraphTextureKey Depth { get; } = depth;

        public void Update(SceneRenderer renderer, in GpuFrame frame, Entity camera)
        {
            _renderer = renderer;
            _frame = frame;
            _camera = camera;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(Depth, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            _renderer!.PrepareFrame(in _frame, _camera);
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphDepthStencilAttachment(Depth, WGPULoadOp.Clear));
            _renderer.EncodeDepthPrepass(in _frame, renderPass);
        }
    }

    private sealed class ForwardOpaqueState(RenderGraphTextureKey color, RenderGraphTextureKey depth)
    {
        private SceneRenderer? _renderer;
        private GpuFrame _frame;
        private WGPULoadOp _colorLoadOp;
        private bool _colorCacheable = true;

        public RenderGraphTextureKey Color { get; } = color;
        public RenderGraphTextureKey Depth { get; } = depth;

        public void Update(SceneRenderer renderer, in GpuFrame frame, WGPULoadOp colorLoadOp, bool colorCacheable)
        {
            _renderer = renderer;
            _frame = frame;
            _colorLoadOp = colorLoadOp;
            _colorCacheable = colorCacheable;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration
                .Write(Color, RenderGraphTextureUsage.RenderAttachment)
                .Write(Depth, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(Color, _colorLoadOp, Cacheable: _colorCacheable),
                new WgpuReactiveRenderGraphDepthStencilAttachment(Depth, WGPULoadOp.Load));
            _renderer!.EncodeForwardOpaque(in _frame, renderPass);
        }
    }
}

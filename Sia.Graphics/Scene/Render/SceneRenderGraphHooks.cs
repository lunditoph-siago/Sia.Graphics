using Sia;
using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public static class SceneRenderGraphHooks
{
    private static readonly RenderGraphBufferKey _clusterConfigKey = new("scene-cluster-config");
    private static readonly RenderGraphBufferKey _clusteredLightsKey = new("scene-clustered-lights");
    private static readonly RenderGraphBufferKey _lightGridKey = new("scene-light-grid");
    private static readonly RenderGraphBufferKey _lightIndexListKey = new("scene-light-index-list");
    private static readonly RenderGraphTextureKey _shadowAtlasKey = new("scene-shadow-atlas");

    public static void UseShadowPasses(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        SceneRenderer renderer,
        in GpuFrame frame,
        ShadowAtlasConfig shadowConfig)
    {
        var atlasDescriptor = new RenderGraphTextureDescriptor(
            "shadow-atlas", RenderGraphTextureFormat.Depth32Float,
            shadowConfig.TileResolution, shadowConfig.TileResolution,
            depthOrArrayLayers: (uint)shadowConfig.LayerCount,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding);
        hooks.UseImportedRenderGraphTexture(registry, _shadowAtlasKey, atlasDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(
            registry, _shadowAtlasKey, renderer.ShadowAtlas.Texture.GetWgpu<WGPUTexture>());

        for (var layer = 0; layer < shadowConfig.LayerCount; layer++) {
            var pass = new RenderGraphPassKey($"scene-shadow-layer-{layer}");
            var state = hooks.UseRef(() => new ShadowLayerState(layer));
            state.Value.Update(renderer, in frame);

            hooks.UseRenderGraphPass(registry, pass, state.Value.Name, state.Value.Declare);
            hooks.UseWgpuRenderGraphPassHandler(registry, pass, state.Value.Render);
        }
    }

    public static void UseClusterLightCullingPass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        SceneRenderer renderer,
        in GpuFrame frame,
        ClusterGridConfig clusterConfig,
        ShadowAtlasConfig shadowConfig,
        Entity cameraEntity,
        RenderGraphPassKey pass)
    {
        renderer.PrepareFrame(in frame, cameraEntity);
        renderer.PrepareLighting(in frame, clusterConfig, shadowConfig, cameraEntity);

        hooks.UseImportedRenderGraphBuffer(
            registry, _clusterConfigKey,
            new RenderGraphBufferDescriptor("cluster-config", ClusterConfigGpu.Stride, RenderGraphBufferUsage.Uniform));
        hooks.UseImportedRenderGraphBufferBinding(
            registry, _clusterConfigKey, renderer.ClusterBuffers.ConfigBuffer.GetWgpu<WGPUBuffer>());

        hooks.UseImportedRenderGraphBuffer(
            registry, _clusteredLightsKey,
            new RenderGraphBufferDescriptor(
                "clustered-lights", renderer.Lights.ClusteredCapacity, RenderGraphBufferUsage.Storage));
        hooks.UseImportedRenderGraphBufferBinding(
            registry, _clusteredLightsKey, renderer.Lights.ClusteredBuffer.GetWgpu<WGPUBuffer>());

        hooks.UseImportedRenderGraphBuffer(
            registry, _lightGridKey,
            new RenderGraphBufferDescriptor(
                "light-grid", renderer.ClusterBuffers.LightGridSize, RenderGraphBufferUsage.Storage));
        hooks.UseImportedRenderGraphBufferBinding(
            registry, _lightGridKey, renderer.ClusterBuffers.LightGridBuffer.GetWgpu<WGPUBuffer>());

        hooks.UseImportedRenderGraphBuffer(
            registry, _lightIndexListKey,
            new RenderGraphBufferDescriptor(
                "light-index-list", renderer.ClusterBuffers.LightIndexListCapacity, RenderGraphBufferUsage.Storage));
        hooks.UseImportedRenderGraphBufferBinding(
            registry, _lightIndexListKey, renderer.ClusterBuffers.LightIndexListBuffer.GetWgpu<WGPUBuffer>());

        var state = hooks.UseRef(() => new ClusterCullingState());
        state.Value.Update(renderer, in frame, clusterConfig);

        hooks.UseComputeRenderGraphPass(registry, pass, "scene-cluster-culling", state.Value.Declare);
        hooks.UseWgpuRenderGraphPassHandler(registry, pass, state.Value.Render);
    }

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

    private sealed class ClusterCullingState
    {
        private SceneRenderer? _renderer;
        private GpuFrame _frame;
        private ClusterGridConfig? _clusterConfig;

        public void Update(SceneRenderer renderer, in GpuFrame frame, ClusterGridConfig clusterConfig)
        {
            _renderer = renderer;
            _frame = frame;
            _clusterConfig = clusterConfig;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration
                .Read(_clusterConfigKey, RenderGraphBufferUsage.Uniform)
                .Read(_clusteredLightsKey, RenderGraphBufferUsage.Storage)
                .Write(_lightGridKey, RenderGraphBufferUsage.Storage)
                .Write(_lightIndexListKey, RenderGraphBufferUsage.Storage);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var computePass = context.GetOrBeginComputePass();
            _renderer!.EncodeClusterLightCulling(in _frame, _clusterConfig!, computePass);
            Wgpu.EndComputePass(computePass);
            Wgpu.Release(ref computePass);
        }
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
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphDepthStencilAttachment(Depth, WGPULoadOp.Clear));
            _renderer!.EncodeDepthPrepass(in _frame, renderPass);
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
                .Write(Depth, RenderGraphTextureUsage.RenderAttachment)
                .Read(_clusterConfigKey, RenderGraphBufferUsage.Uniform)
                .Read(_clusteredLightsKey, RenderGraphBufferUsage.Storage)
                .Read(_lightGridKey, RenderGraphBufferUsage.Storage)
                .Read(_lightIndexListKey, RenderGraphBufferUsage.Storage)
                .Read(_shadowAtlasKey, RenderGraphTextureUsage.TextureBinding);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(Color, _colorLoadOp, Cacheable: _colorCacheable),
                new WgpuReactiveRenderGraphDepthStencilAttachment(Depth, WGPULoadOp.Load));
            _renderer!.EncodeForwardOpaque(in _frame, renderPass);
        }
    }

    private sealed class ShadowLayerState(int layer)
    {
        private SceneRenderer? _renderer;
        private GpuFrame _frame;

        public string Name { get; } = $"scene-shadow-layer-{layer}";

        public void Update(SceneRenderer renderer, in GpuFrame frame)
        {
            _renderer = renderer;
            _frame = frame;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(
                _shadowAtlasKey, RenderGraphTextureUsage.RenderAttachment,
                new RenderGraphTextureSubresourceRange(0, 1, (uint)layer, 1));

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphDepthStencilAttachment(
                    _shadowAtlasKey, WGPULoadOp.Clear,
                    Subresources: new RenderGraphTextureSubresourceRange(0, 1, (uint)layer, 1),
                    Cacheable: false));
            _renderer!.EncodeShadowLayer(in _frame, layer, renderPass);
        }
    }
}

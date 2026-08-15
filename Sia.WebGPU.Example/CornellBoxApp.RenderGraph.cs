using Sia;
using Sia.Graphics.Reactive;
using Sia.Graphics.UI;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;
using SiaReactive = Sia.Reactive.Reactive;

namespace Sia.WebGPU.Example;

internal sealed unsafe partial class CornellBoxApp
{
    private static readonly RenderGraphTextureKey _accumReadKey = new("accum-read");
    private static readonly RenderGraphTextureKey _accumWriteKey = new("accum-write");
    private static readonly RenderGraphTextureKey _surfaceKey = new("surface");
    private static readonly RenderGraphBufferKey _uniformsKey = new("uniforms");
    private static readonly RenderGraphPassKey _pathPassKey = new("path");
    private static readonly RenderGraphPassKey _presentPassKey = new("present");
    private static readonly RenderGraphPassKey _uiPassKey = new("ui");

    private World? _renderGraphWorld;
    private WgpuRenderGraphRegistry? _renderGraph;
    private ReactiveMount<RenderGraphProps>? _renderGraphMount;

    private WgpuReactiveRenderGraphPassHandler? _pathPassHandler;
    private WgpuReactiveRenderGraphPassHandler? _presentPassHandler;

    private readonly Dictionary<WgpuHandle<WGPUTextureView>, WgpuHandle<WGPUBindGroup>>
        _samplingBindGroups = [];
    private readonly HashSet<WgpuHandle<WGPUTextureView>> _samplingBindGroupsUsedThisFrame = [];

    private void InitializeRenderGraph()
    {
        _renderGraphWorld = new World();
        _renderGraph = _renderGraphWorld.ConfigureWgpuRenderGraph(_device, _queue);
        _pathPassHandler = ExecutePathPass;
        _presentPassHandler = ExecutePresentPass;
    }

    private void UpdateRenderGraph(WgpuHandle<WGPUTexture> surfaceTexture, int writeIndex)
    {
        var props = new RenderGraphProps(
            this,
            _framebufferWidth,
            _framebufferHeight,
            _accumulationTextures[_readIndex],
            _accumulationTextures[writeIndex],
            surfaceTexture);

        if (_renderGraphMount is not { } mount) {
            _renderGraphMount = _renderGraphWorld!.Mount(RenderGraph, props);
            return;
        }
        if (mount.Props != props) {
            mount.Update(props);
        }
    }

    private static ReactiveNode RenderGraph(in RenderGraphProps props, ref Hooks hooks)
    {
        var registry = props.App._renderGraph!;

        var accumulationDescriptor = new RenderGraphTextureDescriptor(
            "accumulation",
            RenderGraphTextureFormat.RGBA16Float,
            (uint)props.FramebufferWidth,
            (uint)props.FramebufferHeight,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding);
        hooks.UseImportedRenderGraphTexture(registry, _accumReadKey, accumulationDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(registry, _accumReadKey, props.ReadTexture);
        hooks.UseImportedRenderGraphTexture(registry, _accumWriteKey, accumulationDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(registry, _accumWriteKey, props.WriteTexture);

        var surfaceDescriptor = new RenderGraphTextureDescriptor(
            "surface",
            (RenderGraphTextureFormat)(int)props.App._surfaceFormat,
            (uint)props.FramebufferWidth,
            (uint)props.FramebufferHeight,
            usage: RenderGraphTextureUsage.RenderAttachment);
        hooks.UseImportedRenderGraphTexture(registry, _surfaceKey, surfaceDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(registry, _surfaceKey, props.SurfaceTexture);

        hooks.UseImportedRenderGraphBuffer(
            registry, _uniformsKey,
            new RenderGraphBufferDescriptor("uniforms", _uniformSize, RenderGraphBufferUsage.Uniform));
        hooks.UseImportedRenderGraphBufferBinding(registry, _uniformsKey, props.App._uniformBuffer);

        hooks.UseRenderGraphPass(registry, _pathPassKey, "path", DeclarePathPass);
        hooks.UseWgpuRenderGraphPassHandler(registry, _pathPassKey, props.App._pathPassHandler!);

        hooks.UseRenderGraphPass(registry, _presentPassKey, "present", DeclarePresentPass);
        hooks.UseWgpuRenderGraphPassHandler(registry, _presentPassKey, props.App._presentPassHandler!);

        hooks.UseUiRenderPass(
            registry, props.App._uiWorld!, props.App._uiRenderer!,
            _uiPassKey, _surfaceKey,
            new Size(props.FramebufferWidth, props.FramebufferHeight),
            WGPULoadOp.Load, outputCacheable: false);

        return SiaReactive.None;
    }

    private static void DeclarePathPass(RenderGraphPassDeclarationBuilder pass) => pass
        .Read(_uniformsKey, RenderGraphBufferUsage.Uniform)
        .Read(_accumReadKey, RenderGraphTextureUsage.TextureBinding)
        .Write(_accumWriteKey, RenderGraphTextureUsage.RenderAttachment);

    private static void DeclarePresentPass(RenderGraphPassDeclarationBuilder pass) => pass
        .Read(_uniformsKey, RenderGraphBufferUsage.Uniform)
        .Read(_accumWriteKey, RenderGraphTextureUsage.TextureBinding)
        .Write(_surfaceKey, RenderGraphTextureUsage.RenderAttachment);

    private static readonly WGPUColor _blackClear = new() { R = 0.0, G = 0.0, B = 0.0, A = 1.0 };

    private void ExecutePathPass(WgpuReactiveRenderGraphPassContext context)
    {
        var source = context.GetTextureView(_accumReadKey);
        var renderPass = context.GetOrBeginRenderPass(
            new WgpuReactiveRenderGraphColorAttachment(
                _accumWriteKey, WGPULoadOp.Clear, ClearValue: _blackClear));
        DrawFullscreenTriangle(renderPass, _pathPipeline, source);
    }

    private void ExecutePresentPass(WgpuReactiveRenderGraphPassContext context)
    {
        var source = context.GetTextureView(_accumWriteKey);
        // The surface texture is acquired fresh from the swapchain every frame, and WebGPU
        // is free to recycle the native texture pointer for a different logical frame once
        // presented — caching its view by handle identity can hand back a view of an
        // already-destroyed texture. Never cache it.
        var renderPass = context.GetOrBeginRenderPass(
            new WgpuReactiveRenderGraphColorAttachment(
                _surfaceKey, WGPULoadOp.Clear, ClearValue: _blackClear, Cacheable: false));
        DrawFullscreenTriangle(renderPass, _presentationPipeline, source);
    }

    private void DrawFullscreenTriangle(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        WgpuHandle<WGPURenderPipeline> pipeline,
        WgpuHandle<WGPUTextureView> source)
    {
        var bindGroup = GetOrCreateSamplingBindGroup(source);
        Wgpu.SetRenderPipeline(renderPass, pipeline);
        Wgpu.SetBindGroup(renderPass, 0, bindGroup);
        Wgpu.Draw(renderPass, 3);
    }

    /// <summary>
    /// Reuses the sampling bind group for a given source view across frames instead of
    /// creating/releasing one per draw; ping-pong accumulation views only ever alternate
    /// between two identities once <see cref="WgpuRenderGraphViewCache"/> has warmed up.
    /// </summary>
    private WgpuHandle<WGPUBindGroup> GetOrCreateSamplingBindGroup(
        WgpuHandle<WGPUTextureView> source)
    {
        _samplingBindGroupsUsedThisFrame.Add(source);
        if (_samplingBindGroups.TryGetValue(source, out var bindGroup)) {
            return bindGroup;
        }

        bindGroup = CreateSamplingBindGroup(source);
        _samplingBindGroups.Add(source, bindGroup);
        return bindGroup;
    }

    /// <summary>
    /// Releases sampling bind groups whose source view was not requested this frame (e.g.
    /// because the accumulation textures were recreated on resize). Call once per frame.
    /// </summary>
    private void EndSamplingBindGroupFrame()
    {
        if (_samplingBindGroupsUsedThisFrame.Count < _samplingBindGroups.Count) {
            foreach (var key in _samplingBindGroups.Keys
                .Except(_samplingBindGroupsUsedThisFrame).ToArray()) {
                var bindGroup = _samplingBindGroups[key];
                Wgpu.Release(ref bindGroup);
                _samplingBindGroups.Remove(key);
            }
        }

        _samplingBindGroupsUsedThisFrame.Clear();
    }

    private void DisposeRenderGraph()
    {
        if (_renderGraphMount is { } mount && mount.IsMounted) {
            mount.Unmount();
        }
        _renderGraphWorld?.Dispose();
        _renderGraphMount = null;
        _renderGraph = null;
        _renderGraphWorld = null;

        foreach (var key in _samplingBindGroups.Keys.ToArray()) {
            var bindGroup = _samplingBindGroups[key];
            Wgpu.Release(ref bindGroup);
        }
        _samplingBindGroups.Clear();
        _samplingBindGroupsUsedThisFrame.Clear();
    }

    private readonly record struct RenderGraphProps(
        CornellBoxApp App,
        int FramebufferWidth,
        int FramebufferHeight,
        WgpuHandle<WGPUTexture> ReadTexture,
        WgpuHandle<WGPUTexture> WriteTexture,
        WgpuHandle<WGPUTexture> SurfaceTexture);
}

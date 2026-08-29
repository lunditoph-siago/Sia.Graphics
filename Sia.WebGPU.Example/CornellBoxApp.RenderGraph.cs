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
    private static readonly RenderGraphTextureKey s_AccumReadKey = new("accum-read");
    private static readonly RenderGraphTextureKey s_AccumWriteKey = new("accum-write");
    private static readonly RenderGraphTextureKey s_SurfaceKey = new("surface");
    private static readonly RenderGraphBufferKey s_UniformsKey = new("uniforms");
    private static readonly RenderGraphPassKey s_PathPassKey = new("path");
    private static readonly RenderGraphPassKey s_PresentPassKey = new("present");
    private static readonly RenderGraphPassKey s_UiPassKey = new("ui");

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
            (RenderGraphTextureFormat)(int)k_AccumulationFormat,
            (uint)props.FramebufferWidth,
            (uint)props.FramebufferHeight,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding);
        hooks.UseImportedRenderGraphTexture(registry, s_AccumReadKey, accumulationDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(registry, s_AccumReadKey, props.ReadTexture);
        hooks.UseImportedRenderGraphTexture(registry, s_AccumWriteKey, accumulationDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(registry, s_AccumWriteKey, props.WriteTexture);

        var surfaceDescriptor = new RenderGraphTextureDescriptor(
            "surface",
            (RenderGraphTextureFormat)(int)props.App._surfaceFormat,
            (uint)props.FramebufferWidth,
            (uint)props.FramebufferHeight,
            usage: RenderGraphTextureUsage.RenderAttachment);
        hooks.UseImportedRenderGraphTexture(registry, s_SurfaceKey, surfaceDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(registry, s_SurfaceKey, props.SurfaceTexture);

        hooks.UseImportedRenderGraphBuffer(
            registry, s_UniformsKey,
            new RenderGraphBufferDescriptor("uniforms", k_UniformSize, RenderGraphBufferUsage.Uniform));
        hooks.UseImportedRenderGraphBufferBinding(registry, s_UniformsKey, props.App._uniformBuffer);

        hooks.UseRenderGraphPass(registry, s_PathPassKey, "path", DeclarePathPass);
        hooks.UseWgpuRenderGraphPassHandler(registry, s_PathPassKey, props.App._pathPassHandler!);

        hooks.UseRenderGraphPass(registry, s_PresentPassKey, "present", DeclarePresentPass);
        hooks.UseWgpuRenderGraphPassHandler(registry, s_PresentPassKey, props.App._presentPassHandler!);

        if (props.App._uiEnabled) {
            hooks.UseUiRenderPass(
                registry, props.App._uiWorld!, props.App._uiRenderer!,
                s_UiPassKey, s_SurfaceKey,
                new Size(props.FramebufferWidth, props.FramebufferHeight),
                WGPULoadOp.Load, outputCacheable: false);
        }

        return SiaReactive.None;
    }

    private static void DeclarePathPass(RenderGraphPassDeclarationBuilder pass) => pass
        .Read(s_UniformsKey, RenderGraphBufferUsage.Uniform)
        .Read(s_AccumReadKey, RenderGraphTextureUsage.TextureBinding)
        .Write(s_AccumWriteKey, RenderGraphTextureUsage.RenderAttachment);

    private static void DeclarePresentPass(RenderGraphPassDeclarationBuilder pass) => pass
        .Read(s_UniformsKey, RenderGraphBufferUsage.Uniform)
        .Read(s_AccumWriteKey, RenderGraphTextureUsage.TextureBinding)
        .Write(s_SurfaceKey, RenderGraphTextureUsage.RenderAttachment);

    private static readonly WGPUColor s_BlackClear = new() { R = 0.0, G = 0.0, B = 0.0, A = 1.0 };

    private void ExecutePathPass(WgpuReactiveRenderGraphPassContext context)
    {
        var source = context.GetTextureView(s_AccumReadKey);
        var renderPass = context.GetOrBeginRenderPass(
            new WgpuReactiveRenderGraphColorAttachment(
                s_AccumWriteKey, WGPULoadOp.Clear, ClearValue: s_BlackClear));
        DrawFullscreenTriangle(renderPass, _pathPipeline, source);
    }

    private void ExecutePresentPass(WgpuReactiveRenderGraphPassContext context)
    {
        var source = context.GetTextureView(s_AccumWriteKey);
        var renderPass = context.GetOrBeginRenderPass(
            new WgpuReactiveRenderGraphColorAttachment(
                s_SurfaceKey, WGPULoadOp.Clear, ClearValue: s_BlackClear, Cacheable: false));
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

    private void ReleaseSamplingBindGroups()
    {
        foreach (var key in _samplingBindGroups.Keys.ToArray()) {
            var bindGroup = _samplingBindGroups[key];
            Wgpu.Release(ref bindGroup);
        }
        _samplingBindGroups.Clear();
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

        ReleaseSamplingBindGroups();
    }

    private readonly record struct RenderGraphProps(
        CornellBoxApp App,
        int FramebufferWidth,
        int FramebufferHeight,
        WgpuHandle<WGPUTexture> ReadTexture,
        WgpuHandle<WGPUTexture> WriteTexture,
        WgpuHandle<WGPUTexture> SurfaceTexture);
}

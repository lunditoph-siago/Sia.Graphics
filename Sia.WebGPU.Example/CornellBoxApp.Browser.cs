using System.Runtime.InteropServices.JavaScript;
using Sia.Graphics;
using Sia.Window;

namespace Sia.WebGPU.Example;

#if BROWSER
internal sealed partial class CornellBoxApp
{
    private const string k_ShaderAssetRoot = "spirv/Sia.WebGPU.Example.";
    private SpirvShaderAssets? _spirvShaderAssets;
    private double? _previousAnimationFrameTime;

    public async Task RunAsync()
    {
        await InitializeAsync();

        Console.WriteLine("Cornell Box path tracer controls:");
        Console.WriteLine("  arrows orbit | W/S dolly | -/= exposure | [/] samples");
        Console.WriteLine("  ,/. bounces | O/P aperture | R reset | Esc close");
        Console.WriteLine("  F2 text gallery | F3 gallery load | R refresh gallery text");

        await RunCanvasLoopAsync();
    }

    private bool RenderAnimationFrame(double timestampMilliseconds)
    {
        if (ShouldClose()) {
            return false;
        }

        PollEvents();

        var currentTime = timestampMilliseconds / 1000.0;
        var deltaTime = _previousAnimationFrameTime is double previousTime
            ? (float)System.Math.Min(currentTime - previousTime, 0.1)
            : 0f;
        _previousAnimationFrameTime = currentTime;

        HandleInput(deltaTime);
        UpdateUi();
        if (ResizeIfNeeded()) {
            RenderFrame();
            Wgpu.ProcessEvents(_instance);
            UpdateWindowTitle(currentTime);
        }

        return !ShouldClose();
    }

    private async Task InitializeAsync()
    {
        _browserCanvas = CreateBrowserCanvas();
        var initialFrame = await NextCanvasFrame(_browserCanvas);
        await Task.Yield();
        var dimensions = ParseCanvasFrame(initialFrame);
        var initialSize = new WindowSize((int)dimensions[1], (int)dimensions[2]);
        InitializeWindow(
            new WindowDescriptor(
                initialSize.Width,
                initialSize.Height,
                "Sia.WebGPU · Cornell Box Path Tracer",
                Resizable: true));

        _instance = Wgpu.CreateInstance();

        _adapter = await AwaitBrowserGpuAsync(RequestBrowserAdapterAsync());

        _device = await AwaitBrowserGpuAsync(Wgpu.RequestDeviceAsync(_adapter));
        _queue = Wgpu.GetQueue(_device);
        _maxCanvasDimension = checked((int)Wgpu.GetLimits(_device).MaxTextureDimension2D);
        _target = await WgpuRenderTarget.FromCanvasAsync(_instance, _device, _queue, "canvas",
            GetCanvasPresentation() == "normal" ? WgpuCanvasPresentation.Readback : WgpuCanvasPresentation.Direct);
        _surfaceFormat = _target.Format;
        ApplyCanvasFrame(dimensions);

        _useSpirvShaders = GetShaderFormat() == "spirv";
        if (_useSpirvShaders) _spirvShaderAssets = await LoadSpirvShaderAssetsAsync();
        CreateUniformBuffer();
        CreatePipelines();
        InitializeUi();
        InitializeRenderGraph();
        ResizeIfNeeded(force: true);
    }

    private async Task<T> AwaitBrowserGpuAsync<T>(Task<T> request)
    {
        do {
            await NextCanvasFrame(_browserCanvas!);
            await Task.Yield();
        } while (!request.IsCompleted);
        return await request;
    }

    private static async Task<SpirvShaderAssets> LoadSpirvShaderAssetsAsync()
    {
        Console.WriteLine("Loading generated C# SPIR-V shader assets.");
        var assets = await Task.WhenAll(
            LoadSpirvAssetAsync(
                $"{k_ShaderAssetRoot}CornellBoxRasterShaders.FullscreenVertex.spv"),
            LoadSpirvAssetAsync(
                $"{k_ShaderAssetRoot}CornellBoxPathTracerShaders.PathFragment.spv"),
            LoadSpirvAssetAsync(
                $"{k_ShaderAssetRoot}CornellBoxPathTracerShaders.PresentFragment.spv"));
        return new SpirvShaderAssets(assets[0], assets[1], assets[2]);
    }

    private static async Task<byte[]> LoadSpirvAssetAsync(string path) =>
        Convert.FromBase64String(await LoadBinaryBase64(path));

    [JSImport("loadBinaryBase64", "main.js")]
    private static partial Task<string> LoadBinaryBase64(string path);

    private sealed record SpirvShaderAssets(
        byte[] FullscreenVertex,
        byte[] PathFragment,
        byte[] PresentFragment);

    private void CreateSpirvPipelines(SpirvShaderAssets shaders)
    {
        WgpuHandle<WGPUShaderModule> vertex = default, path = default, present = default;
        try {
            vertex = Wgpu.CreateSpirvShaderModule(_device, shaders.FullscreenVertex, "C# fullscreen vertex");
            path = Wgpu.CreateSpirvShaderModule(_device, shaders.PathFragment, "C# path fragment");
            present = Wgpu.CreateSpirvShaderModule(_device, shaders.PresentFragment, "C# present fragment");
            _pathPipeline = CreateRenderPipeline(
                vertex, "FullscreenVertex", path, "PathFragment", k_AccumulationFormat);
            _presentationPipeline = CreateRenderPipeline(
                vertex, "FullscreenVertex", present, "PresentFragment", _surfaceFormat);
        }
        finally {
            Wgpu.Release(ref present);
            Wgpu.Release(ref path);
            Wgpu.Release(ref vertex);
        }
    }

    [JSImport("shaderFormat", "main.js")]
    private static partial string GetShaderFormat();

    private async Task<WgpuHandle<WGPUAdapter>> RequestBrowserAdapterAsync()
    {
        try {
            return await Wgpu.RequestAdapterAsync(_instance, BuildAdapterOptions());
        }
        catch (WgpuException coreError) {
            try {
                return await Wgpu.RequestAdapterAsync(
                    _instance,
                    BuildAdapterOptions(
                        WGPUFeatureLevel.Compatibility,
                        WGPUPowerPreference.Undefined));
            }
            catch (WgpuException compatibilityError) {
                throw new WgpuException(
                    $"Browser adapter requests failed. Core: {coreError.Message} " +
                    $"Compatibility: {compatibilityError.Message}");
            }
        }
    }
}
#endif

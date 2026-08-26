using System.Runtime.InteropServices.JavaScript;
using Sia.GLFW;
using Sia.Window;

namespace Sia.WebGPU.Example;

#if BROWSER
internal sealed partial class CornellBoxApp
{
    private double? _previousAnimationFrameTime;

    public async Task RunAsync()
    {
        await InitializeAsync();

        Console.WriteLine("Cornell Box path tracer controls:");
        Console.WriteLine("  arrows orbit | W/S dolly | -/= exposure | [/] samples");
        Console.WriteLine("  ,/. bounces | O/P aperture | R reset | Esc close");

        await RunAnimationFrameLoopAsync();
    }

    private bool RenderAnimationFrame(double timestampMilliseconds)
    {
        if (Glfw.ShouldClose(_window)) {
            return false;
        }

        ResizeWindowToCanvas();
        Glfw.PollEvents();

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

        return !Glfw.ShouldClose(_window);
    }

    private async Task InitializeAsync()
    {
        Glfw.Initialize();
        _glfwInitialized = true;
        var initialSize = GetCanvasSize();
        _window = Glfw.CreateWindow(
            new WindowDescriptor(
                initialSize.Width,
                initialSize.Height,
                "Sia.WebGPU · Cornell Box Path Tracer",
                Resizable: true),
            new GlfwWindowOptions(ClientApi.NoApi));

        _instance = Wgpu.CreateSpirvInstance();
        _surface = CreateSurface(_instance, _window);

        _adapter = await RequestBrowserAdapterAsync();

        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;

        _device = await Wgpu.RequestDeviceAsync(_adapter);
        _queue = Wgpu.GetQueue(_device);
        _browserVertexSpirv = Convert.FromBase64String(
            await LoadBinaryBase64($"spirv/{_rasterShaderArtifactName}.spv"));

        CreateUniformBuffer();
        CreatePipelines();
        InitializeUi();
        InitializeRenderGraph();
        ResizeIfNeeded(force: true);
    }

    private void ResizeWindowToCanvas()
    {
        var target = GetCanvasSize();
        var current = Glfw.GetSize(_window);
        if (target.Width != current.Width || target.Height != current.Height) {
            Glfw.SetSize(_window, target);
        }
    }

    private static WindowSize GetCanvasSize() =>
        new(GetCanvasWidth(), GetCanvasHeight());

    [JSImport("getCanvasWidth", "main.js")]
    private static partial int GetCanvasWidth();

    [JSImport("getCanvasHeight", "main.js")]
    private static partial int GetCanvasHeight();

    [JSImport("loadBinaryBase64", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> LoadBinaryBase64(string path);

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

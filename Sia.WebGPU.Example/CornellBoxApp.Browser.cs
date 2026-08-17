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
        _window = Glfw.CreateWindow(
            new WindowDescriptor(
                _initialWidth,
                _initialHeight,
                "Sia.WebGPU · Cornell Box Path Tracer",
                Resizable: true),
            new GlfwWindowOptions(ClientApi.NoApi));

        _instance = Wgpu.CreateInstance();
        _surface = CreateSurface(_instance, _window);

        _adapter = await Wgpu.RequestAdapterAsync(_instance, BuildAdapterOptions());

        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;

        _device = await Wgpu.RequestDeviceAsync(_adapter);
        _queue = Wgpu.GetQueue(_device);

        CreateUniformBuffer();
        CreatePipelines();
        InitializeUi();
        ResizeIfNeeded(force: true);
    }
}
#endif

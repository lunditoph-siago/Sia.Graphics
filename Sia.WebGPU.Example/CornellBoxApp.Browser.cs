using System.Diagnostics;
using Sia.GLFW;
using Sia.Window;

namespace Sia.WebGPU.Example;

#if BROWSER
internal sealed partial class CornellBoxApp
{
    public async Task RunAsync()
    {
        await InitializeAsync();

        Console.WriteLine("Cornell Box path tracer controls:");
        Console.WriteLine("  arrows orbit | W/S dolly | -/= exposure | [/] samples");
        Console.WriteLine("  ,/. bounces | O/P aperture | R reset | Esc close");

        var clock = Stopwatch.StartNew();
        var previousTime = clock.Elapsed.TotalSeconds;

        while (!Glfw.ShouldClose(_window)) {
            Glfw.PollEvents();

            var currentTime = clock.Elapsed.TotalSeconds;
            var deltaTime = (float)Math.Min(currentTime - previousTime, 0.1);
            previousTime = currentTime;

            HandleInput(deltaTime);
            if (!ResizeIfNeeded()) {
                await RequestAnimationFrameAsync();
                continue;
            }

            RenderFrame();
            Wgpu.ProcessEvents(_instance);
            UpdateWindowTitle(currentTime);

            await RequestAnimationFrameAsync();
        }
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
        ResizeIfNeeded(force: true);
    }
}
#endif

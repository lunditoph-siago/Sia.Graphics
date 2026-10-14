using Sia;
using Sia.GLFW;
using Sia.Window;

namespace Sia.WebGPU.Example;

internal sealed partial class CornellBoxApp
{
    private World? _windowWorld;
    private Entity? _windowEntity;

    private void InitializeWindow(in WindowDescriptor descriptor)
    {
        var world = new World();
        try {
#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
            var entity = world.Create();
            entity.Add(new WindowState(new(descriptor.Width, descriptor.Height), new(descriptor.Width, descriptor.Height), false));
            _windowWorld = world;
            _windowEntity = entity;
#else
            var entity = world.CreateGlfwWindow(
                in descriptor,
                new GlfwWindowOptions(ClientApi.NoApi));
            _windowWorld = world;
            _windowEntity = entity;
            _window = entity.Get<GlfwWindow>();
#endif
            InitializeApplicationInput();
        }
        catch {
            _windowWorld = null;
            _windowEntity = null;
            _window = default;
            world.Dispose();
            throw;
        }
    }

    private WindowSize GetWindowSize() =>
#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
        _windowEntity!.Value.Get<WindowState>().Size;
#else
        Glfw.GetSize(_window);
#endif

    private WindowSize GetFramebufferSize() =>
#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
        _windowEntity!.Value.Get<WindowState>().FramebufferSize;
#else
        Glfw.GetFramebufferSize(_window);
#endif

    private bool ShouldClose() =>
#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
        _windowEntity!.Value.Get<WindowState>().CloseRequested;
#else
        Glfw.ShouldClose(_window);
#endif

    private void PollEvents()
    {
#if !(BROWSER && SIA_WEBGPU_BACKEND_DAWN)
        Glfw.PollEvents();
#endif
    }

    private void RequestClose()
    {
#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
        _windowEntity!.Value.Get<WindowState>() = _windowEntity!.Value.Get<WindowState>() with { CloseRequested = true };
        _windowWorld!.Send(_windowEntity!.Value, new WindowEvents.CloseRequested());
#else
        Glfw.RequestClose(_window);
#endif
    }

    private void SetWindowTitle(string title)
    {
#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
        SetBrowserTitle(title);
#else
        Glfw.SetTitle(_window, title);
#endif
    }

    private void DisposeWindow()
    {
#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
        if (_browserCanvas is not null) {
            DisposeBrowserCanvas(_browserCanvas);
            _browserCanvas.Dispose();
            _browserCanvas = null;
        }
#endif
        DisposeApplicationInput();
        _windowWorld?.Dispose();
        _windowWorld = null;
        _windowEntity = null;
        _window = default;
    }
}

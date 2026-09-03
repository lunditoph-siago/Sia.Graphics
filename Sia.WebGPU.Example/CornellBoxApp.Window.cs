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
            var entity = world.CreateGlfwWindow(
                in descriptor,
                new GlfwWindowOptions(ClientApi.NoApi));
            _windowWorld = world;
            _windowEntity = entity;
            _window = entity.Get<GlfwWindow>();
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

    private void DisposeWindow()
    {
        DisposeApplicationInput();
        _windowWorld?.Dispose();
        _windowWorld = null;
        _windowEntity = null;
        _window = default;
    }
}

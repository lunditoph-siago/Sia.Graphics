using Sia;
using Sia.Input;

namespace Sia.WebGPU.Example;

internal sealed partial class CornellBoxApp
{
    private WorldDispatcher.Listener<InputEvents.KeyPressed>? _keyPressedListener;
    private WorldDispatcher.Listener<InputEvents.KeyReleased>? _keyReleasedListener;

    private void InitializeApplicationInput()
    {
        if (_windowWorld is null)
            throw new InvalidOperationException("Window world must be initialized first.");

        _keyPressedListener = OnKeyPressed;
        _keyReleasedListener = OnKeyReleased;
        _windowWorld.Dispatcher.Listen(_keyPressedListener);
        _windowWorld.Dispatcher.Listen(_keyReleasedListener);
    }

    private void DisposeApplicationInput()
    {
        if (_windowWorld is { } world) {
            if (_keyPressedListener is { } keyPressed)
                world.Dispatcher.Unlisten(keyPressed);
            if (_keyReleasedListener is { } keyReleased)
                world.Dispatcher.Unlisten(keyReleased);
        }

        _keyPressedListener = null;
        _keyReleasedListener = null;
        _downKeys.Clear();
        _pressedKeys.Clear();
    }

    private bool OnKeyPressed(Entity target, in InputEvents.KeyPressed message)
    {
        if (target == _windowEntity && !UiCapturesKeyboard()) {
            _downKeys.Add(message.Key);
            _pressedKeys.Add(message.Key);
        }
        return false;
    }

    private bool OnKeyReleased(Entity target, in InputEvents.KeyReleased message)
    {
        if (target == _windowEntity)
            _downKeys.Remove(message.Key);
        return false;
    }
}

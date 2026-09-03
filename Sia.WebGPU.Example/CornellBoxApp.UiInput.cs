using Sia.UI;

namespace Sia.WebGPU.Example;

internal sealed partial class CornellBoxApp
{
    private UiInputBinding? _uiInputBinding;

    private void InitializeUiInput()
    {
        var windowWorld = _windowWorld
            ?? throw new InvalidOperationException("Window world must be initialized first.");
        var windowEntity = _windowEntity
            ?? throw new InvalidOperationException("Window entity must be initialized first.");
        var uiWorld = _uiWorld
            ?? throw new InvalidOperationException("UI world must be initialized first.");

        _uiInputBinding = new UiInputBinding(
            windowWorld,
            windowEntity,
            uiWorld);
    }

    private void UpdateUiInput(Size windowSize, Size viewportSize) =>
        _uiInputBinding?.SetPointerSpace(windowSize, viewportSize);

    private void DisposeUiInput()
    {
        _uiInputBinding?.Dispose();
        _uiInputBinding = null;
    }

    private bool UiCapturesKeyboard() =>
        _uiInputBinding?.CapturesKeyboard ?? false;
}

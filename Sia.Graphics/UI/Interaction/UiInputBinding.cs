using Sia;
using Sia.Input;

namespace Sia.Graphics.UI;

public sealed class UiInputBinding : IDisposable
{
    private readonly World _sourceWorld;
    private readonly Entity _source;
    private readonly World _uiWorld;
    private readonly WorldDispatcher.Listener<InputEvents.KeyPressed> _keyPressed;
    private readonly WorldDispatcher.Listener<InputEvents.KeyRepeated> _keyRepeated;
    private readonly WorldDispatcher.Listener<InputEvents.TextEntered> _textEntered;
    private readonly WorldDispatcher.Listener<InputEvents.MouseButtonPressed> _mouseButtonPressed;
    private readonly WorldDispatcher.Listener<InputEvents.MouseButtonReleased> _mouseButtonReleased;
    private readonly WorldDispatcher.Listener<InputEvents.MouseMoved> _mouseMoved;
    private readonly WorldDispatcher.Listener<InputEvents.MouseEntered> _mouseEntered;
    private readonly WorldDispatcher.Listener<InputEvents.MouseExited> _mouseExited;
    private readonly WorldDispatcher.Listener<InputEvents.MouseScrolled> _mouseScrolled;
    private MousePosition _pointerPosition;
    private Size _sourceSize = new(1f, 1f);
    private Size _viewportSize = new(1f, 1f);
    private bool _disposed;

    public bool CapturesKeyboard =>
        _uiWorld.AcquireAddon<UiFocusState>().Focused is { IsValid: true };

    public UiInputBinding(World sourceWorld, Entity source, World uiWorld)
    {
        ArgumentNullException.ThrowIfNull(sourceWorld);
        ArgumentNullException.ThrowIfNull(uiWorld);
        if (!source.IsValid)
            throw new ArgumentException("The input source entity is not valid.", nameof(source));

        _sourceWorld = sourceWorld;
        _source = source;
        _uiWorld = uiWorld;
        _keyPressed = OnKeyPressed;
        _keyRepeated = OnKeyRepeated;
        _textEntered = OnTextEntered;
        _mouseButtonPressed = OnMouseButtonPressed;
        _mouseButtonReleased = OnMouseButtonReleased;
        _mouseMoved = OnMouseMoved;
        _mouseEntered = OnMouseEntered;
        _mouseExited = OnMouseExited;
        _mouseScrolled = OnMouseScrolled;

        var dispatcher = sourceWorld.Dispatcher;
        dispatcher.Listen(_keyPressed);
        dispatcher.Listen(_keyRepeated);
        dispatcher.Listen(_textEntered);
        dispatcher.Listen(_mouseButtonPressed);
        dispatcher.Listen(_mouseButtonReleased);
        dispatcher.Listen(_mouseMoved);
        dispatcher.Listen(_mouseEntered);
        dispatcher.Listen(_mouseExited);
        dispatcher.Listen(_mouseScrolled);
    }

    public void SetPointerSpace(Size sourceSize, Size viewportSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceSize.Width <= 0f || sourceSize.Height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(sourceSize));
        if (viewportSize.Width <= 0f || viewportSize.Height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(viewportSize));

        _sourceSize = sourceSize;
        _viewportSize = viewportSize;
        UpdatePointerPosition();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var dispatcher = _sourceWorld.Dispatcher;
        dispatcher.Unlisten(_keyPressed);
        dispatcher.Unlisten(_keyRepeated);
        dispatcher.Unlisten(_textEntered);
        dispatcher.Unlisten(_mouseButtonPressed);
        dispatcher.Unlisten(_mouseButtonReleased);
        dispatcher.Unlisten(_mouseMoved);
        dispatcher.Unlisten(_mouseEntered);
        dispatcher.Unlisten(_mouseExited);
        dispatcher.Unlisten(_mouseScrolled);
    }

    private bool OnKeyPressed(Entity target, in InputEvents.KeyPressed message)
    {
        if (target == _source)
            Input.PressKey(message.Key, message.Modifiers);
        return false;
    }

    private bool OnKeyRepeated(Entity target, in InputEvents.KeyRepeated message)
    {
        if (target == _source)
            Input.PressKey(message.Key, message.Modifiers);
        return false;
    }

    private bool OnTextEntered(Entity target, in InputEvents.TextEntered message)
    {
        if (target == _source)
            Input.EnterText(message.CodePoint);
        return false;
    }

    private bool OnMouseButtonPressed(
        Entity target,
        in InputEvents.MouseButtonPressed message)
    {
        if (target == _source && message.Button == MouseButton.Left)
            Pointer.SetButtonDown(true);
        return false;
    }

    private bool OnMouseButtonReleased(
        Entity target,
        in InputEvents.MouseButtonReleased message)
    {
        if (target == _source && message.Button == MouseButton.Left)
            Pointer.SetButtonDown(false);
        return false;
    }

    private bool OnMouseMoved(Entity target, in InputEvents.MouseMoved message)
    {
        if (target != _source)
            return false;

        _pointerPosition = message.Position;
        UpdatePointerPosition();
        return false;
    }

    private bool OnMouseScrolled(Entity target, in InputEvents.MouseScrolled message)
    {
        if (target == _source)
            Input.Scroll(message.Delta);
        return false;
    }

    private bool OnMouseEntered(Entity target, in InputEvents.MouseEntered _)
    {
        if (target == _source)
            UpdatePointerPosition();
        return false;
    }

    private bool OnMouseExited(Entity target, in InputEvents.MouseExited _)
    {
        if (target == _source)
            Pointer.Cancel(new Point(float.NegativeInfinity, float.NegativeInfinity));
        return false;
    }

    private void UpdatePointerPosition()
    {
        Pointer.MoveTo(new Point(
            (float)(_pointerPosition.X * _viewportSize.Width / _sourceSize.Width),
            (float)(_pointerPosition.Y * _viewportSize.Height / _sourceSize.Height)));
    }

    private UiInputState Input => _uiWorld.AcquireAddon<UiInputState>();

    private UiPointerState Pointer => _uiWorld.AcquireAddon<UiPointerState>();
}

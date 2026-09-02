using Sia;

namespace Sia.Graphics.UI;

public sealed class UiPointerState : IAddon
{
    private readonly record struct Snapshot(Point Position, bool ButtonDown);

    private readonly Queue<Snapshot> _pending = [];

    public Point Position { get; set; }
    public bool ButtonDown { get; set; }

    internal void MoveTo(Point position)
    {
        Position = position;
        Push();
    }

    internal void SetButtonDown(bool buttonDown)
    {
        ButtonDown = buttonDown;
        Push();
    }

    internal void Cancel(Point position)
    {
        Position = position;
        ButtonDown = false;
        Push();
    }

    private void Push() => _pending.Enqueue(new Snapshot(Position, ButtonDown));

    internal bool TryRead(out Point position, out bool buttonDown)
    {
        if (_pending.TryDequeue(out var snapshot)) {
            position = snapshot.Position;
            buttonDown = snapshot.ButtonDown;
            return true;
        }

        position = default;
        buttonDown = default;
        return false;
    }
}

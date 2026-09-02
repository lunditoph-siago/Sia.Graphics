using Sia;
using Sia.Input;
using System.Text;

namespace Sia.Graphics.UI;

public readonly record struct UiKeyInput(Key Key, KeyModifiers Modifiers);

public sealed class UiInputState : IAddon
{
    private readonly record struct PendingInput(uint? CodePoint, UiKeyInput Key);

    private readonly Queue<PendingInput> _pending = [];
    private ScrollDelta _scrollDelta;

    public void EnterText(uint codePoint)
    {
        if (!Rune.IsValid((int)codePoint))
            throw new ArgumentOutOfRangeException(nameof(codePoint));
        _pending.Enqueue(new PendingInput(codePoint, default));
    }

    public void PressKey(Key key, KeyModifiers modifiers = default) =>
        _pending.Enqueue(new PendingInput(null, new UiKeyInput(key, modifiers)));

    public void Scroll(ScrollDelta delta) =>
        _scrollDelta = new ScrollDelta(
            _scrollDelta.X + delta.X,
            _scrollDelta.Y + delta.Y);

    internal bool TryRead(out uint? codePoint, out UiKeyInput key)
    {
        if (_pending.TryDequeue(out var input)) {
            codePoint = input.CodePoint;
            key = input.Key;
            return true;
        }

        codePoint = null;
        key = default;
        return false;
    }

    internal ScrollDelta ConsumeScrollDelta()
    {
        var delta = _scrollDelta;
        _scrollDelta = default;
        return delta;
    }
}

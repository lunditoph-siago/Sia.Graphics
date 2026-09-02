using Sia;
using Sia.Input;

namespace Sia.Graphics.UI;

public record struct TextInput(
    string Value,
    int Caret = -1,
    int MaxLength = 1024,
    bool Multiline = false);

public readonly record struct TextInputChanged(string Value, int Caret) : IEvent;

public readonly record struct TextInputSubmitted(string Value) : IEvent;

public readonly record struct FocusChanged(bool Focused) : IEvent;

public sealed class TextInputSystem() : SystemBase(Matchers.Of<TextInput>())
{
    public override void Initialize(World world)
    {
        world.Dispatcher.Listen<PointerClick>((Entity target, in PointerClick _) => {
            var next = target.IsValid && target.Contains<TextInput>()
                && !target.Contains<Disabled>()
                ? target
                : (Entity?)null;
            SetFocus(world, next);
            return false;
        });
    }

    public override void Execute(WorldContext context, IEntityQuery query)
    {
        _ = query;
        var world = context.World;
        var focus = world.AcquireAddon<UiFocusState>();
        if (focus.Focused is not { IsValid: true } target
            || !target.Contains<TextInput>() || target.Contains<Disabled>()) {
            SetFocus(world, null);
            Drain(world.AcquireAddon<UiInputState>());
            return;
        }

        var input = world.AcquireAddon<UiInputState>();
        while (input.TryRead(out var codePoint, out var key)) {
            if (codePoint is { } text)
                Insert(world, target, char.ConvertFromUtf32((int)text));
            else
                ApplyKey(world, target, key.Key);
        }
    }

    private static void ApplyKey(World world, Entity target, Key key)
    {
        if (!TryGetLive(target, out var input))
            return;

        var caret = NormalizeCaret(input);
        switch (key) {
            case Key.Backspace:
                if (caret > 0) {
                    var previous = PreviousCodePoint(input.Value, caret);
                    input.Value = input.Value.Remove(previous, caret - previous);
                    input.Caret = previous;
                    Commit(world, target, input);
                }
                break;
            case Key.Delete:
                if (caret < input.Value.Length) {
                    var next = NextCodePoint(input.Value, caret);
                    input.Value = input.Value.Remove(caret, next - caret);
                    input.Caret = caret;
                    Commit(world, target, input);
                }
                break;
            case Key.Left:
                MoveCaret(world, target, input, PreviousCodePoint(input.Value, caret));
                break;
            case Key.Right:
                MoveCaret(world, target, input, NextCodePoint(input.Value, caret));
                break;
            case Key.Home:
                MoveCaret(world, target, input, 0);
                break;
            case Key.End:
                MoveCaret(world, target, input, input.Value.Length);
                break;
            case Key.Enter:
            case Key.KeypadEnter:
                if (input.Multiline)
                    Insert(world, target, "\n");
                else
                    world.Dispatcher.Send(target, new TextInputSubmitted(input.Value));
                break;
            case Key.Escape:
            case Key.Tab:
                SetFocus(world, null);
                break;
        }
    }

    private static void Insert(World world, Entity target, string value)
    {
        if (!TryGetLive(target, out var input))
            return;

        var caret = NormalizeCaret(input);
        var available = System.Math.Max(0, input.MaxLength - input.Value.Length);
        if (available == 0)
            return;
        if (value.Length > available)
            value = value[..available];
        if (value.Length == 0 || char.IsHighSurrogate(value[^1]))
            return;

        input.Value = input.Value.Insert(caret, value);
        input.Caret = caret + value.Length;
        Commit(world, target, input);
    }

    private static void MoveCaret(World world, Entity target, TextInput input, int caret)
    {
        if (input.Caret == caret)
            return;
        input.Caret = caret;
        Commit(world, target, input);
    }

    private static void Commit(World world, Entity target, TextInput input)
    {
        target.Set(input);
        world.Dispatcher.Send(target, new TextInputChanged(input.Value, input.Caret));
    }

    private static bool TryGetLive(Entity target, out TextInput input)
    {
        if (target.IsValid && target.Contains<TextInput>()) {
            input = target.Get<TextInput>();
            return true;
        }

        input = default;
        return false;
    }

    private static void SetFocus(World world, Entity? next)
    {
        var focus = world.AcquireAddon<UiFocusState>();
        if (focus.Focused == next)
            return;

        if (focus.Focused is { IsValid: true } previous) {
            if (previous.Contains<Focused>())
                previous.Remove<Focused>();
            world.Dispatcher.Send(previous, new FocusChanged(false));
        }

        focus.Focused = next;
        if (next is { } current) {
            if (!current.Contains<Focused>())
                current.Add<Focused>();
            world.Dispatcher.Send(current, new FocusChanged(true));
        }
    }

    private static int NormalizeCaret(TextInput input) =>
        System.Math.Clamp(input.Caret < 0 ? input.Value.Length : input.Caret, 0, input.Value.Length);

    private static int PreviousCodePoint(string value, int caret)
    {
        if (caret <= 0)
            return 0;
        var previous = caret - 1;
        return previous > 0 && char.IsLowSurrogate(value[previous])
            && char.IsHighSurrogate(value[previous - 1])
            ? previous - 1
            : previous;
    }

    private static int NextCodePoint(string value, int caret)
    {
        if (caret >= value.Length)
            return value.Length;
        return caret + 1 < value.Length && char.IsHighSurrogate(value[caret])
            && char.IsLowSurrogate(value[caret + 1])
            ? caret + 2
            : caret + 1;
    }

    private static void Drain(UiInputState input)
    {
        while (input.TryRead(out _, out _)) { }
    }
}

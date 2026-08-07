namespace Sia.Graphics.UI;

public readonly record struct UiRect(Val Left, Val Right, Val Top, Val Bottom)
{
    public static readonly UiRect Zero = All(Val.Zero);

    public static UiRect All(Val value) => new(value, value, value, value);

    public static UiRect Axes(Val horizontal, Val vertical) =>
        new(horizontal, horizontal, vertical, vertical);

    public static UiRect Horizontal(Val value) => new(value, value, Val.Zero, Val.Zero);
    public static UiRect Vertical(Val value) => new(Val.Zero, Val.Zero, value, value);

    public Val this[UiAxis axis, bool trailing] =>
        axis == UiAxis.Horizontal
            ? (trailing ? Right : Left)
            : (trailing ? Bottom : Top);
}

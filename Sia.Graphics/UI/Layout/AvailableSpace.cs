namespace Sia.Graphics.UI;

public enum AvailableSpaceKind
{
    Definite,
    MinContent,
    MaxContent
}

public readonly record struct AvailableSpace(AvailableSpaceKind Kind, float Value)
{
    public static readonly AvailableSpace MinContent = new(AvailableSpaceKind.MinContent, 0f);
    public static readonly AvailableSpace MaxContent = new(AvailableSpaceKind.MaxContent, 0f);

    public static AvailableSpace Definite(float value) => new(AvailableSpaceKind.Definite, value);

    public bool IsDefinite => Kind == AvailableSpaceKind.Definite;

    public float UnwrapOr(float alt) => IsDefinite ? Value : alt;

    public AvailableSpace MaybeSet(float? value) => value is { } v ? Definite(v) : this;
}

public readonly record struct AvailableSize(AvailableSpace Width, AvailableSpace Height)
{
    public AvailableSpace this[UiAxis axis] => axis == UiAxis.Horizontal ? Width : Height;

    public AvailableSize WithAxis(UiAxis axis, AvailableSpace value) =>
        axis == UiAxis.Horizontal ? this with { Width = value } : this with { Height = value };
}

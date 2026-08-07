namespace Sia.Graphics.UI;

public enum ValKind
{
    Auto,
    Px,
    Percent,
    Vw,
    Vh,
    VMin,
    VMax
}

public readonly record struct Val(ValKind Kind, float Value)
{
    public static readonly Val Auto = new(ValKind.Auto, 0f);
    public static readonly Val Zero = Px(0f);

    public static Val Px(float value) => new(ValKind.Px, value);
    public static Val Percent(float value) => new(ValKind.Percent, value);
    public static Val Vw(float value) => new(ValKind.Vw, value);
    public static Val Vh(float value) => new(ValKind.Vh, value);
    public static Val VMin(float value) => new(ValKind.VMin, value);
    public static Val VMax(float value) => new(ValKind.VMax, value);

    public bool IsAuto => Kind == ValKind.Auto;

    public float? Resolve(float scaleFactor, float basis, Size viewport) => Kind switch {
        ValKind.Auto => null,
        ValKind.Px => Value * scaleFactor,
        ValKind.Percent => basis * (Value / 100f),
        ValKind.Vw => viewport.Width * (Value / 100f),
        ValKind.Vh => viewport.Height * (Value / 100f),
        ValKind.VMin => MathF.Min(viewport.Width, viewport.Height) * (Value / 100f),
        ValKind.VMax => MathF.Max(viewport.Width, viewport.Height) * (Value / 100f),
        _ => null
    };

    public float ResolveOrZero(float scaleFactor, float basis, Size viewport) =>
        Resolve(scaleFactor, basis, viewport) ?? 0f;
}

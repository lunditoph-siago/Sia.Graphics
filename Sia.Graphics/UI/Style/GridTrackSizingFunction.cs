namespace Sia.Graphics.UI;

public enum MinTrackSizingFunctionKind
{
    Fixed,
    MinContent,
    MaxContent,
    Auto
}

public readonly record struct MinTrackSizingFunction(MinTrackSizingFunctionKind Kind, Val Value)
{
    public static readonly MinTrackSizingFunction Auto = new(MinTrackSizingFunctionKind.Auto, Val.Zero);
    public static readonly MinTrackSizingFunction MinContent = new(MinTrackSizingFunctionKind.MinContent, Val.Zero);
    public static readonly MinTrackSizingFunction MaxContent = new(MinTrackSizingFunctionKind.MaxContent, Val.Zero);

    public static MinTrackSizingFunction Fixed(Val value) => new(MinTrackSizingFunctionKind.Fixed, value);
}

public enum MaxTrackSizingFunctionKind
{
    Fixed,
    MinContent,
    MaxContent,
    FitContent,
    Auto,
    Fraction
}

public readonly record struct MaxTrackSizingFunction(MaxTrackSizingFunctionKind Kind, Val Value, float Fraction)
{
    public static readonly MaxTrackSizingFunction Auto = new(MaxTrackSizingFunctionKind.Auto, Val.Zero, 0f);
    public static readonly MaxTrackSizingFunction MinContent = new(MaxTrackSizingFunctionKind.MinContent, Val.Zero, 0f);
    public static readonly MaxTrackSizingFunction MaxContent = new(MaxTrackSizingFunctionKind.MaxContent, Val.Zero, 0f);

    public static MaxTrackSizingFunction Fixed(Val value) => new(MaxTrackSizingFunctionKind.Fixed, value, 0f);
    public static MaxTrackSizingFunction FitContent(Val value) => new(MaxTrackSizingFunctionKind.FitContent, value, 0f);
    public static MaxTrackSizingFunction FromFraction(float fraction) => new(MaxTrackSizingFunctionKind.Fraction, Val.Zero, fraction);

    public bool IsFlexible => Kind == MaxTrackSizingFunctionKind.Fraction;
}

public readonly record struct GridTrackSizingFunction(MinTrackSizingFunction Min, MaxTrackSizingFunction Max)
{
    public static GridTrackSizingFunction Fixed(Val value) => new(MinTrackSizingFunction.Fixed(value), MaxTrackSizingFunction.Fixed(value));
    public static readonly GridTrackSizingFunction Auto = new(MinTrackSizingFunction.Auto, MaxTrackSizingFunction.Auto);
    public static readonly GridTrackSizingFunction MinContent = new(MinTrackSizingFunction.MinContent, MaxTrackSizingFunction.MinContent);
    public static readonly GridTrackSizingFunction MaxContent = new(MinTrackSizingFunction.MaxContent, MaxTrackSizingFunction.MaxContent);
    public static GridTrackSizingFunction Fraction(float fraction) => new(MinTrackSizingFunction.Auto, MaxTrackSizingFunction.FromFraction(fraction));
    public static GridTrackSizingFunction FitContent(Val value) => new(MinTrackSizingFunction.Auto, MaxTrackSizingFunction.FitContent(value));
}

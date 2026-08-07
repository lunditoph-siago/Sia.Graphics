namespace Sia.Graphics.UI;

public readonly record struct PartialSize(float? Width, float? Height)
{
    public static readonly PartialSize Unknown = new(null, null);

    public float? this[UiAxis axis] => axis == UiAxis.Horizontal ? Width : Height;
}

public interface ILayoutMeasure
{
    float? Baseline { get; }

    Size Measure(PartialSize knownDimensions, AvailableSize availableSpace);
}

namespace Sia.Graphics.UI;

public readonly record struct LayoutInput(
    PartialSize KnownDimensions,
    PartialSize ParentSize,
    AvailableSize AvailableSpace,
    Size Viewport,
    float ScaleFactor,
    bool PerformLayout)
{
    public LayoutInput WithPerformLayout(bool performLayout) => this with { PerformLayout = performLayout };

    public float ResolveOrZero(Val value, float basis) => value.ResolveOrZero(ScaleFactor, basis, Viewport);

    public float? Resolve(Val value, float basis) => value.Resolve(ScaleFactor, basis, Viewport);
}

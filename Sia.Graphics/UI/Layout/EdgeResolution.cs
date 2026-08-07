namespace Sia.Graphics.UI;

internal static class EdgeResolution
{
    public static BorderEdges Resolve(UiRect rect, LayoutInput input, float parentWidth) => new(
        input.ResolveOrZero(rect.Left, parentWidth),
        input.ResolveOrZero(rect.Right, parentWidth),
        input.ResolveOrZero(rect.Top, parentWidth),
        input.ResolveOrZero(rect.Bottom, parentWidth));

    public static float Clamp(float value, float? min, float? max)
    {
        if (min is { } mn) value = MathF.Max(value, mn);
        if (max is { } mx) value = MathF.Min(value, mx);
        return value;
    }

    public static float? ResolveBorderBoxSize(
        Node style,
        float? knownBorderBoxSize,
        Val authoredSize,
        LayoutInput input,
        float basis,
        float borderAndPadding)
    {
        if (knownBorderBoxSize is { } known)
            return MathF.Max(0f, known);
        return input.Resolve(authoredSize, basis) is { } authored
            ? ToBorderBox(style, authored, borderAndPadding)
            : null;
    }

    public static float? ResolveContentConstraint(
        Node style, Val constraint, LayoutInput input, float basis, float borderAndPadding)
    {
        if (input.Resolve(constraint, basis) is not { } resolved)
            return null;
        return style.BoxSizing == BoxSizing.BorderBox
            ? MathF.Max(0f, resolved - borderAndPadding)
            : MathF.Max(0f, resolved);
    }

    public static float ToBorderBox(Node style, float authoredSize, float borderAndPadding) =>
        MathF.Max(0f, style.BoxSizing == BoxSizing.BorderBox
            ? authoredSize
            : authoredSize + borderAndPadding);

    public static void ApplyAspectRatio(Node style, ref float? width, ref float? height)
    {
        if (style.AspectRatio is not { } ratio || !float.IsFinite(ratio) || ratio <= 0f)
            return;
        if (width is { } resolvedWidth && height == null)
            height = resolvedWidth / ratio;
        else if (height is { } resolvedHeight && width == null)
            width = resolvedHeight * ratio;
    }
}

namespace Sia.Graphics.UI;

public enum FlexDirection
{
    Row,
    Column,
    RowReverse,
    ColumnReverse
}

public enum FlexWrap
{
    NoWrap,
    Wrap,
    WrapReverse
}

public static class FlexDirectionExtensions
{
    public static UiAxis MainAxis(this FlexDirection direction) =>
        direction is FlexDirection.Row or FlexDirection.RowReverse
            ? UiAxis.Horizontal
            : UiAxis.Vertical;

    public static UiAxis CrossAxis(this FlexDirection direction) =>
        direction.MainAxis() == UiAxis.Horizontal ? UiAxis.Vertical : UiAxis.Horizontal;

    public static bool IsReversed(this FlexDirection direction) =>
        direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse;
}

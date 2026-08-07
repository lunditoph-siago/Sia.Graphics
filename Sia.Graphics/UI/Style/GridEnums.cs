namespace Sia.Graphics.UI;

public enum GridAutoFlow
{
    Row,
    Column,
    RowDense,
    ColumnDense
}

public static class GridAutoFlowExtensions
{
    public static UiAxis PrimaryAxis(this GridAutoFlow flow) =>
        flow is GridAutoFlow.Row or GridAutoFlow.RowDense ? UiAxis.Horizontal : UiAxis.Vertical;

    public static bool IsDense(this GridAutoFlow flow) =>
        flow is GridAutoFlow.RowDense or GridAutoFlow.ColumnDense;
}

public readonly record struct GridPlacement(int? Start, int? End, int? Span)
{
    public static readonly GridPlacement Auto = new(null, null, null);

    public static GridPlacement Line(int line) => new(line, null, null);
    public static GridPlacement FromTo(int start, int end) => new(start, end, null);
    public static GridPlacement SpanCount(int span) => new(null, null, span);
}

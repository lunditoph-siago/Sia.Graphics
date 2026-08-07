namespace Sia.Graphics.UI;

public struct LayoutResult
{
    public Point Location;
    public Size Size;
    public Size ContentSize;
    public BorderEdges Border;
    public BorderEdges Padding;
    public float? Baseline;
    public int Order;

    public static readonly LayoutResult Zero = new() {
        Location = Point.Zero,
        Size = Size.Zero,
        ContentSize = Size.Zero,
        Border = BorderEdges.Zero,
        Padding = BorderEdges.Zero,
        Baseline = null,
        Order = 0
    };
}

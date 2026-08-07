namespace Sia.Graphics.UI;

public enum OverflowAxis
{
    Visible,
    Clip,
    Hidden,
    Scroll
}

public readonly record struct Overflow(OverflowAxis X, OverflowAxis Y)
{
    public static readonly Overflow Visible = new(OverflowAxis.Visible, OverflowAxis.Visible);
    public static readonly Overflow Clip = new(OverflowAxis.Clip, OverflowAxis.Clip);
    public static readonly Overflow Hidden = new(OverflowAxis.Hidden, OverflowAxis.Hidden);
    public static readonly Overflow Scroll = new(OverflowAxis.Scroll, OverflowAxis.Scroll);

    public bool IsVisibleX => X == OverflowAxis.Visible;
    public bool IsVisibleY => Y == OverflowAxis.Visible;

    // Any non-visible overflow axis clips content to the node's bounds.
    public bool ClipsX => X != OverflowAxis.Visible;
    public bool ClipsY => Y != OverflowAxis.Visible;
}

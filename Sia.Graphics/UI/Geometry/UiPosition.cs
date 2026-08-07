namespace Sia.Graphics.UI;

public readonly record struct UiPosition(float AnchorX, float AnchorY, Val OffsetX, Val OffsetY)
{
    public static readonly UiPosition TopLeft = new(0f, 0f, Val.Zero, Val.Zero);
    public static readonly UiPosition Center = new(0.5f, 0.5f, Val.Zero, Val.Zero);

    public static UiPosition FromAnchor(float anchorX, float anchorY) =>
        new(anchorX, anchorY, Val.Zero, Val.Zero);
}

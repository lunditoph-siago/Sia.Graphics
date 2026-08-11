using System.Runtime.InteropServices;

namespace Sia.Graphics.UI;

[StructLayout(LayoutKind.Sequential)]
public struct UiVertex
{
    public float PositionX, PositionY, PositionZ;
    public float UvX, UvY;
    public float ColorR, ColorG, ColorB, ColorA;
    public uint Flags;
    public float RadiusXTopLeft, RadiusXTopRight, RadiusXBottomRight, RadiusXBottomLeft;
    public float RadiusYTopLeft, RadiusYTopRight, RadiusYBottomRight, RadiusYBottomLeft;
    public float BorderLeft, BorderTop, BorderRight, BorderBottom;
    public float SizeX, SizeY;
    public float PointX, PointY;

    public static UiVertex Create(
        Point position, Point uv, Color color, UiVertexFlags flags,
        ResolvedBorderRadius radius, BorderEdges border, Size size, Point pointFromCenter) => new() {
        PositionX = position.X, PositionY = position.Y, PositionZ = 0f,
        UvX = uv.X, UvY = uv.Y,
        ColorR = color.R, ColorG = color.G, ColorB = color.B, ColorA = color.A,
        Flags = (uint)flags,
        RadiusXTopLeft = radius.TopLeft, RadiusXTopRight = radius.TopRight,
        RadiusXBottomRight = radius.BottomRight, RadiusXBottomLeft = radius.BottomLeft,
        RadiusYTopLeft = radius.TopLeft, RadiusYTopRight = radius.TopRight,
        RadiusYBottomRight = radius.BottomRight, RadiusYBottomLeft = radius.BottomLeft,
        BorderLeft = border.Left, BorderTop = border.Top, BorderRight = border.Right, BorderBottom = border.Bottom,
        SizeX = size.Width, SizeY = size.Height,
        PointX = pointFromCenter.X, PointY = pointFromCenter.Y
    };
}

[Flags]
public enum UiVertexFlags : uint
{
    None = 0,
    Textured = 1,
    BorderLeft = 256,
    BorderTop = 512,
    BorderRight = 1024,
    BorderBottom = 2048,
    Invert = 4096
}

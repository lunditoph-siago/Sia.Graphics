using System.Runtime.InteropServices;
using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

[StructLayout(LayoutKind.Sequential, Size = 96)]
internal struct UiPrimitive
{
    public float TransformM11, TransformM12, TransformM21, TransformM22;
    public float TranslateX, TranslateY, TopLeftX, TopLeftY;
    public float SizeX, SizeY, UvMinLayer, UvMinY;
    public uint RadiusTop, RadiusBottom;
    public uint BorderLeftTop, BorderRightBottom;
    public float ClipLeft, ClipTop, ClipRight, ClipBottom;
    public uint PackedColor;

    public static UiPrimitive Create(in ExtractedUiNode node)
    {
        var transform = node.Transform ?? UiGlobalTransform.Identity;
        var clip = node.ClipRect ?? UiClipRect.Unbounded;
        return new UiPrimitive {
            TransformM11 = transform.M11,
            TransformM12 = transform.M12,
            TransformM21 = transform.M21,
            TransformM22 = transform.M22,
            TranslateX = transform.Tx,
            TranslateY = transform.Ty,
            TopLeftX = node.TopLeft.X,
            TopLeftY = node.TopLeft.Y,
            SizeX = node.Size.Width,
            SizeY = node.Size.Height,
            UvMinLayer = node.UvMin.X + (node.TextureKey is FontAtlas atlas ? atlas.Layer : 0),
            UvMinY = node.UvMin.Y,
            RadiusTop = PackHalf(node.BorderRadius.TopLeft, node.BorderRadius.TopRight),
            RadiusBottom = PackHalf(node.BorderRadius.BottomRight, node.BorderRadius.BottomLeft),
            BorderLeftTop = PackHalf(node.Border.Left, node.Border.Top),
            BorderRightBottom = PackHalf(node.Border.Right, node.Border.Bottom),
            ClipLeft = clip.X,
            ClipTop = clip.Y,
            ClipRight = clip.Right,
            ClipBottom = clip.Bottom,
            PackedColor = PackColor(node.Color)
        };
    }

    private static uint PackHalf(float first, float second) =>
        BitConverter.HalfToUInt16Bits((Half)first)
        | (uint)BitConverter.HalfToUInt16Bits((Half)second) << 16;

    private static uint PackColor(Color color) =>
        PackUnorm(color.R)
        | PackUnorm(color.G) << 8
        | PackUnorm(color.B) << 16
        | PackUnorm(color.A) << 24;

    private static uint PackUnorm(float value) =>
        (uint)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}

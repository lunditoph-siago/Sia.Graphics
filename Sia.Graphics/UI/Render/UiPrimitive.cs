using System.Runtime.InteropServices;
using Sia.Graphics.Text;

namespace Sia.Graphics.UI;

[StructLayout(LayoutKind.Sequential)]
internal struct UiPrimitive
{
    public float TransformM11, TransformM12, TransformM21, TransformM22;
    public float TranslateX, TranslateY, TopLeftX, TopLeftY;
    public float SizeX, SizeY, UvMinX, UvMinY;
    public float UvMaxX, UvMaxY, ColorR, ColorG;
    public float ColorB, ColorA, RadiusTopLeft, RadiusTopRight;
    public float RadiusBottomRight, RadiusBottomLeft, BorderLeft, BorderTop;
    public float BorderRight, BorderBottom, ClipLeft, ClipTop;
    public float ClipRight, ClipBottom, Flags, TextureLayer;

    public static UiPrimitive Create(in ExtractedUiNode node)
    {
        var transform = node.Transform ?? UiGlobalTransform.Identity;
        var clip = node.ClipRect ?? new UiClipRect(-1e9f, -1e9f, 2e9f, 2e9f);
        var flags = UiPrimitiveFlags.None;
        if (node.Kind == ExtractedUiNodeKind.Border) {
            if (node.Border.Left > 0f)
                flags |= UiPrimitiveFlags.BorderLeft;
            if (node.Border.Top > 0f)
                flags |= UiPrimitiveFlags.BorderTop;
            if (node.Border.Right > 0f)
                flags |= UiPrimitiveFlags.BorderRight;
            if (node.Border.Bottom > 0f)
                flags |= UiPrimitiveFlags.BorderBottom;
        }

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
            UvMinX = node.UvMin.X,
            UvMinY = node.UvMin.Y,
            UvMaxX = node.UvMax.X,
            UvMaxY = node.UvMax.Y,
            ColorR = node.Color.R,
            ColorG = node.Color.G,
            ColorB = node.Color.B,
            ColorA = node.Color.A,
            RadiusTopLeft = node.BorderRadius.TopLeft,
            RadiusTopRight = node.BorderRadius.TopRight,
            RadiusBottomRight = node.BorderRadius.BottomRight,
            RadiusBottomLeft = node.BorderRadius.BottomLeft,
            BorderLeft = node.Border.Left,
            BorderTop = node.Border.Top,
            BorderRight = node.Border.Right,
            BorderBottom = node.Border.Bottom,
            ClipLeft = clip.X,
            ClipTop = clip.Y,
            ClipRight = clip.Right,
            ClipBottom = clip.Bottom,
            Flags = BitConverter.UInt32BitsToSingle((uint)flags),
            TextureLayer = BitConverter.UInt32BitsToSingle(
                node.TextureKey is FontAtlas atlas ? (uint)atlas.Layer : 0u)
        };
    }
}

[Flags]
internal enum UiPrimitiveFlags : uint
{
    None = 0,
    BorderLeft = 1,
    BorderTop = 2,
    BorderRight = 4,
    BorderBottom = 8
}

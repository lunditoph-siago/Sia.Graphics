namespace Sia.Graphics.Text;

public sealed class GlyphOutline
{
    public List<GlyphContour> Contours { get; } = [];
    public float MinX { get; set; }
    public float MinY { get; set; }
    public float MaxX { get; set; }
    public float MaxY { get; set; }
    public float AdvanceWidth { get; set; }
}

namespace Sia.Graphics.UI;

public readonly record struct UiGlobalTransform(float M11, float M12, float M21, float M22, float Tx, float Ty)
{
    public static readonly UiGlobalTransform Identity = new(1f, 0f, 0f, 1f, 0f, 0f);

    public static UiGlobalTransform Translation(float x, float y) => new(1f, 0f, 0f, 1f, x, y);

    public Point Transform(Point p) => new(
        M11 * p.X + M21 * p.Y + Tx,
        M12 * p.X + M22 * p.Y + Ty);

    public Point InverseTransform(Point p)
    {
        var x = p.X - Tx;
        var y = p.Y - Ty;
        var determinant = M11 * M22 - M21 * M12;
        if (MathF.Abs(determinant) <= 1e-8f)
            return new Point(float.NaN, float.NaN);
        return new Point(
            (M22 * x - M21 * y) / determinant,
            (-M12 * x + M11 * y) / determinant);
    }

    public static UiGlobalTransform operator *(UiGlobalTransform a, UiGlobalTransform b) => new(
        a.M11 * b.M11 + a.M21 * b.M12,
        a.M12 * b.M11 + a.M22 * b.M12,
        a.M11 * b.M21 + a.M21 * b.M22,
        a.M12 * b.M21 + a.M22 * b.M22,
        a.M11 * b.Tx + a.M21 * b.Ty + a.Tx,
        a.M12 * b.Tx + a.M22 * b.Ty + a.Ty);
}

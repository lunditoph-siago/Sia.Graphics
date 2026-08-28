namespace Sia.Graphics.UI;

public static class UiOrthographicProjection
{
    internal const ulong k_UniformByteSize = 16 * sizeof(float);

    public static float[] Build(Size viewport)
    {
        var w = viewport.Width > 0f ? viewport.Width : 1f;
        var h = viewport.Height > 0f ? viewport.Height : 1f;

        return [
            2f / w, 0f, 0f, 0f,
            0f, -2f / h, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1f, 1f, 0f, 1f
        ];
    }
}

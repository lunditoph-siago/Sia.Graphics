using Sia.Spirv;

namespace Smoke.Modules;

internal static class RasterShaders
{
    [SpirvVertexShader]
    public static void FullscreenVertex()
    {
        var vertexIndex = Gpu.VertexIndex;
        var x = -1.0f;
        var y = -1.0f;
        if (vertexIndex == 1u) {
            x = 3.0f;
        }
        else if (vertexIndex == 2u) {
            y = 3.0f;
        }

        Gpu.SetPosition(x, y, 0.0f, 1.0f);
        Gpu.SetOutput(0, x * 0.5f + 0.5f, y * 0.5f + 0.5f, 0.0f, 1.0f);
    }

    [SpirvFragmentShader]
    public static void SolidFragment()
    {
        var u = Gpu.GetInput(0, 0);
        var v = Gpu.GetInput(0, 1);
        var x = Gpu.GetFragmentPosition(0);
        Gpu.SetOutput(0, u, v, x, 1.0f);
    }
}

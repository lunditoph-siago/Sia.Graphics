using Sia.Spirv;

namespace Sia.WebGPU.Example;

internal static class CornellBoxRasterShaders
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
        Gpu.SetOutput(
            0,
            x * 0.5f + 0.5f,
            y * 0.5f + 0.5f,
            0.0f,
            1.0f);
    }
}

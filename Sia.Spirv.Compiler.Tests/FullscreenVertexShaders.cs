using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class FullscreenVertexShaders
{
    [SpirvVertexShader]
    public static FullscreenVertexOutput Vertex(FullscreenVertexInput input)
    {
        var x = -1.0f;
        var y = -1.0f;
        if (input.VertexIndex == 1u) {
            x = 3.0f;
        }
        else if (input.VertexIndex == 2u) {
            y = 3.0f;
        }

        return new FullscreenVertexOutput(
            new float2(x * 0.5f + 0.5f, y * 0.5f + 0.5f),
            input.InstanceIndex,
            new float4(x, y, 0.0f, 1.0f));
    }
}

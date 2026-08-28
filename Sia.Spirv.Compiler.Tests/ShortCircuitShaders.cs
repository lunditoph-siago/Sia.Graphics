using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class ShortCircuitShaders
{
    [SpirvFragmentShader]
    public static void Fragment(float value, float limit)
    {
        var isInside = value >= -1.0f &&
            value <= 1.0f &&
            limit > 0.001f &&
            value < limit;
        if (isInside) {
            Gpu.SetOutput(0, 1.0f, 1.0f, 1.0f, 1.0f);
            return;
        }

        Gpu.SetOutput(0, 0.0f, 0.0f, 0.0f, 1.0f);
    }
}

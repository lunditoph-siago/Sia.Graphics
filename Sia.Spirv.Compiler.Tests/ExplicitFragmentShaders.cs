using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class ExplicitFragmentShaders
{
    [SpirvFragmentShader]
    public static ExplicitFragmentOutput Fragment(ExplicitFragmentInput input)
    {
        var facing = input.FrontFacing ? 1.0f : 0.0f;
        if (input.MaterialId == 0u) {
            facing *= 0.5f;
        }
        return new ExplicitFragmentOutput(
            new float4(
                input.TextureCoordinate.x,
                facing,
                input.TextureCoordinate.y,
                1.0f),
            input.Position.z);
    }
}

using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class ExplicitFragmentShaders
{
    [SpirvFragmentShader]
    public static ExplicitFragmentOutput Fragment(ExplicitFragmentInput input) =>
        new(new float4(
            input.Position.x,
            input.Position.y,
            input.Position.z,
            input.Position.w));
}

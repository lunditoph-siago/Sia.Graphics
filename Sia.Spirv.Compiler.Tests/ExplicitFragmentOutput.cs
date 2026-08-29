using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal readonly struct ExplicitFragmentOutput(float4 color)
{
    [Location(0)]
    public readonly float4 Color = color;
}

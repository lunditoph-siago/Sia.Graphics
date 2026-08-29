using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal readonly struct ExplicitFragmentOutput(float4 color, float depth)
{
    [Location(0)]
    public readonly float4 Color = color;

    [FragmentDepth]
    public readonly float Depth = depth;
}

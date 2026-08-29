using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal readonly struct ExplicitFragmentInput(float4 textureCoordinate, float4 position)
{
    [Location(0)]
    public readonly float4 TextureCoordinate = textureCoordinate;

    [FragmentPosition]
    public readonly float4 Position = position;
}

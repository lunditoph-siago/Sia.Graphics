using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal readonly struct ExplicitFragmentInput(
    float2 textureCoordinate,
    uint materialId,
    float4 position,
    bool frontFacing)
{
    [Location(0)]
    [Interpolate(InterpolationMode.Linear, InterpolationSampling.Centroid)]
    public readonly float2 TextureCoordinate = textureCoordinate;

    [Location(1)]
    public readonly uint MaterialId = materialId;

    [FragmentPosition]
    public readonly float4 Position = position;

    [FrontFacing]
    public readonly bool FrontFacing = frontFacing;
}

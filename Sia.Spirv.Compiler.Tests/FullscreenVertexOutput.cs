using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal readonly struct FullscreenVertexOutput(
    float2 textureCoordinate,
    uint materialId,
    float4 position)
{
    [Position]
    public readonly float4 Position = position;

    [Location(0)]
    [Interpolate(InterpolationMode.Linear, InterpolationSampling.Centroid)]
    public readonly float2 TextureCoordinate = textureCoordinate;

    [Location(1)]
    public readonly uint MaterialId = materialId;
}

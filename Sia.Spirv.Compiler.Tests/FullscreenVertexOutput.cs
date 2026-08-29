using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal readonly struct FullscreenVertexOutput(float4 textureCoordinate, float4 position)
{
    [Position]
    public readonly float4 Position = position;

    [Location(0)]
    public readonly float4 TextureCoordinate = textureCoordinate;
}

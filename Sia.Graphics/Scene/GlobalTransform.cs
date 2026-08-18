using Sia.Math;

namespace Sia.Graphics.Scene;

public record struct GlobalTransform(float4x4 WorldMatrix)
{
    public static GlobalTransform Identity => new(float4x4.identity);
}

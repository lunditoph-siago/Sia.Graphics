using Sia.Math;

namespace Sia.Graphics.Scene;

public partial record struct Transform([Sia] float3 Position, [Sia] quaternion Rotation, [Sia] float3 Scale)
{
    public static Transform Identity =>
        new(float3.zero, quaternion.identity, new float3(1, 1, 1));

    public readonly float4x4 ToMatrix() => float4x4.TRS(Position, Rotation, Scale);
}

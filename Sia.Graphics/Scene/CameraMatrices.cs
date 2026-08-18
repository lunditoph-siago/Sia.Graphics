using Sia.Math;

namespace Sia.Graphics.Scene;

public record struct CameraMatrices(
    float4x4 View,
    float4x4 Proj,
    float4x4 ViewProj,
    float4x4 InvViewProj,
    float3 WorldPosition,
    Frustum Frustum)
{
    public static CameraMatrices Identity => new(
        float4x4.identity, float4x4.identity, float4x4.identity, float4x4.identity,
        float3.zero, default);
}

using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ClusteredLightGpu(
    float4 PositionRange,
    float4 DirectionKind,
    float4 ColorIntensity,
    float4 SpotAngles)
{
    public const int Stride = 64;
    public const float PointKind = 0.0f;
    public const float SpotKind = 1.0f;

    public static ClusteredLightGpu Point(float3 position, float range, float3 color, float intensity) => new(
        new float4(position, range),
        new float4(float3.zero, PointKind),
        new float4(color, intensity),
        float4.zero);

    public static ClusteredLightGpu Spot(
        float3 position, float3 direction, float range,
        float3 color, float intensity, float innerCos, float outerCos, int shadowLayer = -1) => new(
        new float4(position, range),
        new float4(direction, SpotKind),
        new float4(color, intensity),
        new float4(innerCos, outerCos, shadowLayer, 0.0f));
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DirectionalLightGpu(float4 DirectionPad, float4 ColorIntensity)
{
    public const int Stride = 32;

    public static DirectionalLightGpu Create(float3 direction, float3 color, float intensity) => new(
        new float4(direction, 0.0f),
        new float4(color, intensity));
}

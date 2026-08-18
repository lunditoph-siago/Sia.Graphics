using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct RenderInstance(
    float4x4 WorldMatrix,
    float4x4 NormalMatrix,
    float4 BaseColor,
    float4 MaterialParams,
    float4 Emissive)
{
    public const int Stride = 176;
}

using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct RenderInstance(float4x4 WorldMatrix, float4x4 NormalMatrix, float4 BaseColor)
{
    public const int Stride = 144;
}

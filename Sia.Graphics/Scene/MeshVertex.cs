using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshVertex(float3 Position, float3 Normal, float2 UV)
{
    public const int Stride = 48;
    public const int PositionOffset = 0;
    public const int NormalOffset = 16;
    public const int UVOffset = 32;
}

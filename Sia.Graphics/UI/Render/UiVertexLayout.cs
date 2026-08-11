using System.Runtime.InteropServices;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public static class UiVertexLayout
{
    public readonly record struct AttributeDescriptor(WGPUVertexFormat Format, ulong Offset, uint ShaderLocation);

    public static readonly AttributeDescriptor[] Attributes = [
        new(WGPUVertexFormat.Float32x3, Offset(nameof(UiVertex.PositionX)), 0),
        new(WGPUVertexFormat.Float32x2, Offset(nameof(UiVertex.UvX)), 1),
        new(WGPUVertexFormat.Float32x4, Offset(nameof(UiVertex.ColorR)), 2),
        new(WGPUVertexFormat.Uint32, Offset(nameof(UiVertex.Flags)), 3),
        new(WGPUVertexFormat.Float32x4, Offset(nameof(UiVertex.RadiusXTopLeft)), 4),
        new(WGPUVertexFormat.Float32x4, Offset(nameof(UiVertex.RadiusYTopLeft)), 5),
        new(WGPUVertexFormat.Float32x4, Offset(nameof(UiVertex.BorderLeft)), 6),
        new(WGPUVertexFormat.Float32x2, Offset(nameof(UiVertex.SizeX)), 7),
        new(WGPUVertexFormat.Float32x2, Offset(nameof(UiVertex.PointX)), 8),
    ];

    public static readonly ulong Stride = (ulong)Marshal.SizeOf<UiVertex>();

    private static ulong Offset(string fieldName) => (ulong)Marshal.OffsetOf<UiVertex>(fieldName);
}

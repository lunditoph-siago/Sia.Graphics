using Sia.WebGPU;

namespace Sia.Graphics.Scene;

internal static class SceneVertexLayout
{
    public const int AttributeCount = 3;

    public static void Fill(Span<WGPUVertexAttribute> attributes)
    {
        attributes[0] = WGPUVertexAttribute.Default;
        attributes[0].Format = WGPUVertexFormat.Float32x3;
        attributes[0].Offset = MeshVertex.PositionOffset;
        attributes[0].ShaderLocation = 0;

        attributes[1] = WGPUVertexAttribute.Default;
        attributes[1].Format = WGPUVertexFormat.Float32x3;
        attributes[1].Offset = MeshVertex.NormalOffset;
        attributes[1].ShaderLocation = 1;

        attributes[2] = WGPUVertexAttribute.Default;
        attributes[2].Format = WGPUVertexFormat.Float32x2;
        attributes[2].Offset = MeshVertex.UVOffset;
        attributes[2].ShaderLocation = 2;
    }
}

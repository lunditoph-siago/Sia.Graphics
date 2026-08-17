using Sia.Math;

namespace Sia.Graphics.Scene;

public static partial class ProceduralMesh
{
    public static MeshData Cube(float size = 1.0f)
    {
        var half = size * 0.5f;
        var vertices = new MeshVertex[24];
        var indices = new uint[36];

        Span<(float3 Normal, float3 U, float3 V)> faces = [
            (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),   // +Z
            (new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0)), // -Z
            (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),  // +X
            (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),  // -X
            (new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)),  // +Y
            (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),  // -Y
        ];

        for (var face = 0; face < faces.Length; face++) {
            var (normal, u, v) = faces[face];
            var center = normal * half;
            var vertexBase = face * 4;

            vertices[vertexBase + 0] = new MeshVertex(center - u * half - v * half, normal, new float2(0, 0));
            vertices[vertexBase + 1] = new MeshVertex(center + u * half - v * half, normal, new float2(1, 0));
            vertices[vertexBase + 2] = new MeshVertex(center + u * half + v * half, normal, new float2(1, 1));
            vertices[vertexBase + 3] = new MeshVertex(center - u * half + v * half, normal, new float2(0, 1));

            var indexBase = face * 6;
            var vb = (uint)vertexBase;
            indices[indexBase + 0] = vb + 0;
            indices[indexBase + 1] = vb + 1;
            indices[indexBase + 2] = vb + 2;
            indices[indexBase + 3] = vb + 0;
            indices[indexBase + 4] = vb + 2;
            indices[indexBase + 5] = vb + 3;
        }

        return new MeshData(vertices, indices, ComputeBounds(vertices));
    }
}

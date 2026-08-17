using Sia.Math;

namespace Sia.Graphics.Scene;

public static partial class ProceduralMesh
{
    public static MeshData Plane(float width = 1.0f, float depth = 1.0f, int segments = 1)
    {
        if (segments < 1) {
            throw new ArgumentOutOfRangeException(nameof(segments));
        }

        var verticesPerSide = segments + 1;
        var vertices = new MeshVertex[verticesPerSide * verticesPerSide];
        var halfWidth = width * 0.5f;
        var halfDepth = depth * 0.5f;

        for (var z = 0; z < verticesPerSide; z++) {
            var v = (float)z / segments;
            for (var x = 0; x < verticesPerSide; x++) {
                var u = (float)x / segments;
                vertices[z * verticesPerSide + x] = new MeshVertex(
                    Position: new float3(
                        -halfWidth + u * width,
                        0.0f,
                        -halfDepth + v * depth),
                    Normal: new float3(0, 1, 0),
                    UV: new float2(u, v));
            }
        }

        var indices = new uint[segments * segments * 6];
        var cursor = 0;
        for (var z = 0; z < segments; z++) {
            for (var x = 0; x < segments; x++) {
                var topLeft = (uint)(z * verticesPerSide + x);
                var topRight = topLeft + 1;
                var bottomLeft = (uint)((z + 1) * verticesPerSide + x);
                var bottomRight = bottomLeft + 1;

                indices[cursor++] = topLeft;
                indices[cursor++] = bottomLeft;
                indices[cursor++] = topRight;
                indices[cursor++] = topRight;
                indices[cursor++] = bottomLeft;
                indices[cursor++] = bottomRight;
            }
        }

        return new MeshData(vertices, indices, ComputeBounds(vertices));
    }
}

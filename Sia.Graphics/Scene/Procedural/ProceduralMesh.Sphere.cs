using Sia.Math;

namespace Sia.Graphics.Scene;

public static partial class ProceduralMesh
{
    public static MeshData Sphere(float radius = 0.5f, int latitudeSegments = 16, int longitudeSegments = 32)
    {
        if (latitudeSegments < 2 || longitudeSegments < 3) {
            throw new ArgumentOutOfRangeException(
                latitudeSegments < 2 ? nameof(latitudeSegments) : nameof(longitudeSegments));
        }

        var latVerts = latitudeSegments + 1;
        var lonVerts = longitudeSegments + 1;
        var vertices = new MeshVertex[latVerts * lonVerts];

        for (var lat = 0; lat < latVerts; lat++) {
            var theta = MathF.PI * lat / latitudeSegments;
            var sinTheta = MathF.Sin(theta);
            var cosTheta = MathF.Cos(theta);

            for (var lon = 0; lon < lonVerts; lon++) {
                var phi = 2.0f * MathF.PI * lon / longitudeSegments;
                var direction = new float3(
                    sinTheta * MathF.Cos(phi),
                    cosTheta,
                    sinTheta * MathF.Sin(phi));

                vertices[lat * lonVerts + lon] = new MeshVertex(
                    Position: direction * radius,
                    Normal: direction,
                    UV: new float2((float)lon / longitudeSegments, (float)lat / latitudeSegments));
            }
        }

        var indices = new uint[latitudeSegments * longitudeSegments * 6];
        var cursor = 0;
        for (var lat = 0; lat < latitudeSegments; lat++) {
            for (var lon = 0; lon < longitudeSegments; lon++) {
                var current = (uint)(lat * lonVerts + lon);
                var next = current + 1;
                var below = (uint)((lat + 1) * lonVerts + lon);
                var belowNext = below + 1;

                indices[cursor++] = current;
                indices[cursor++] = next;
                indices[cursor++] = below;
                indices[cursor++] = next;
                indices[cursor++] = belowNext;
                indices[cursor++] = below;
            }
        }

        return new MeshData(vertices, indices, ComputeBounds(vertices));
    }
}

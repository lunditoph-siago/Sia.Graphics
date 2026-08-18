using Sia.Math;

namespace Sia.Graphics.Scene;

public static partial class ProceduralMesh
{
    private static Aabb ComputeBounds(ReadOnlySpan<MeshVertex> vertices)
    {
        var min = vertices[0].Position;
        var max = min;
        for (var i = 1; i < vertices.Length; i++) {
            var p = vertices[i].Position;
            min = math.min(min, p);
            max = math.max(max, p);
        }
        return new Aabb(min, max);
    }
}

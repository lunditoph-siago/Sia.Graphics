using Sia;
using Sia.Math;

namespace Sia.Graphics.Scene;

public sealed class WorldBoundsSystem()
    : SystemBase(Matchers.Of<Bounds, WorldBounds, GlobalTransform>())
{
    public override void Execute(WorldContext context, IEntityQuery query)
    {
        foreach (var entity in query) {
            var local = entity.Get<Bounds>().Local;
            var worldMatrix = entity.Get<GlobalTransform>().WorldMatrix;
            entity.Get<WorldBounds>().World = TransformAabb(local, worldMatrix);
        }
    }

    private static Aabb TransformAabb(Aabb local, float4x4 worldMatrix)
    {
        var min = local.Min;
        var max = local.Max;
        Span<float3> corners = [
            new(min.x, min.y, min.z), new(max.x, min.y, min.z),
            new(min.x, max.y, min.z), new(max.x, max.y, min.z),
            new(min.x, min.y, max.z), new(max.x, min.y, max.z),
            new(min.x, max.y, max.z), new(max.x, max.y, max.z),
        ];

        var first = TransformPoint(worldMatrix, corners[0]);
        var result = new Aabb(first, first);
        for (var i = 1; i < corners.Length; i++) {
            result.Include(TransformPoint(worldMatrix, corners[i]));
        }
        return result;
    }

    private static float3 TransformPoint(float4x4 worldMatrix, float3 point) =>
        math.mul(worldMatrix, new float4(point, 1.0f)).xyz;
}

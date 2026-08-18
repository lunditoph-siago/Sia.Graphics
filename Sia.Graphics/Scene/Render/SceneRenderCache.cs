using Sia;
using Sia.Math;

namespace Sia.Graphics.Scene;

public sealed class SceneRenderCache : SnapshotExtractSystem<RenderInstance>, IAddon
{
    private const int BatchSize = 128;

    private readonly List<MeshHandle> _meshHandles = [];
    private readonly List<Aabb> _worldBounds = [];
    private readonly List<int> _visibleIndices = [];

    void IAddon.OnInitialize(World world) => Initialize(world);

    protected override IEntityMatcher ExtractMatcher =>
        Matchers.Of<Mesh, Material, MeshRenderer, GlobalTransform, WorldBounds>();

    protected override RenderInstance Extract(Entity entity)
    {
        _meshHandles.Add(entity.Get<Mesh>().Handle);
        _worldBounds.Add(entity.Get<WorldBounds>().World);

        var worldMatrix = entity.Get<GlobalTransform>().WorldMatrix;
        var material = entity.Get<Material>();
        return new RenderInstance(
            worldMatrix,
            ComputeNormalMatrix(worldMatrix),
            new float4(material.BaseColor, 1.0f),
            new float4(material.Metallic, material.Roughness, 0.0f, 0.0f),
            new float4(material.EmissiveColor, material.EmissiveStrength));
    }

    public void Refresh()
    {
        _meshHandles.Clear();
        _worldBounds.Clear();
        RunExtract();
    }

    public IReadOnlyList<MeshHandle> MeshHandles => _meshHandles;

    public IReadOnlyList<int> Cull(Frustum frustum)
    {
        _visibleIndices.Clear();
        var count = _worldBounds.Count;

        for (var batchStart = 0; batchStart < count; batchStart += BatchSize) {
            var batchEnd = System.Math.Min(batchStart + BatchSize, count);
            var batchAabb = _worldBounds[batchStart];
            for (var i = batchStart + 1; i < batchEnd; i++) {
                batchAabb.Include(_worldBounds[i]);
            }
            if (!frustum.Intersects(batchAabb)) {
                continue;
            }

            for (var i = batchStart; i < batchEnd; i++) {
                if (frustum.Intersects(_worldBounds[i])) {
                    _visibleIndices.Add(i);
                }
            }
        }

        return _visibleIndices;
    }

    private static float4x4 ComputeNormalMatrix(float4x4 worldMatrix)
    {
        var upper = new float3x3(worldMatrix);
        var normal = math.transpose(math.inverse(upper));
        return new float4x4(normal, float3.zero);
    }
}

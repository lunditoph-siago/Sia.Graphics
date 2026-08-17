using Sia;
using Sia.Math;
using Sia.Reactors;

namespace Sia.Graphics.Scene;

public sealed class TransformSystem() : AddonSystemBase(Matchers.Of<Transform, GlobalTransform, Node<SceneGraph>>())
{
    private readonly HashSet<Entity> _visited = [];

    public override void Initialize(World world)
    {
        base.Initialize(world);
        AddAddon<Hierarchy<SceneGraph>>(world);
    }

    public override void Execute(WorldContext context, IEntityQuery query)
    {
        _visited.Clear();
        foreach (var entity in query) {
            if (entity.Get<Node<SceneGraph>>().Parent is null) {
                Propagate(entity, float4x4.identity, hasParent: false);
            }
        }
    }

    private void Propagate(Entity entity, in float4x4 parentWorld, bool hasParent)
    {
        if (!_visited.Add(entity)) {
            return;
        }

        var local = entity.Get<Transform>().ToMatrix();
        var world = hasParent ? math.mul(parentWorld, local) : local;
        entity.Get<GlobalTransform>().WorldMatrix = world;

        foreach (var child in entity.Get<Node<SceneGraph>>().Children) {
            if (child.Contains<Transform>() && child.Contains<GlobalTransform>()) {
                Propagate(child, world, hasParent: true);
            }
        }
    }
}

using Sia;

namespace Sia.Graphics.UI;

public sealed class UiStackSystem() : SystemBase(Matchers.Of<Node, ComputedNode, UiRoot>())
{
    private long _lastLayoutVersion = -1;

    public override void Execute(World world, IEntityQuery query)
    {
        var changes = world.AcquireAddon<UiChangeTracker>();
        if (_lastLayoutVersion == changes.LayoutVersion)
            return;

        var counter = 0;
        var visited = new HashSet<Entity>();
        foreach (var root in query) {
            Visit(root, ref counter, visited);
        }
        _lastLayoutVersion = changes.LayoutVersion;
    }

    private static void Visit(Entity entity, ref int counter, HashSet<Entity> visited)
    {
        if (!visited.Add(entity))
            return;
        var computed = entity.Get<ComputedNode>();
        var stackIndex = counter++;
        if (computed.StackIndex != stackIndex)
            entity.Set(computed with { StackIndex = stackIndex });

        if (!entity.Contains<UiChildren>())
            return;

        var children = entity.Get<UiChildren>().Value;
        var ordered = children
            .Where(c => c.IsValid && c.Contains<Node>() && c.Contains<ComputedNode>())
            .OrderBy(c => c.Contains<ZIndex>() ? c.Get<ZIndex>().Value : 0);

        foreach (var child in ordered)
            Visit(child, ref counter, visited);
    }
}

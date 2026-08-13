using Sia;

namespace Sia.Graphics.UI;

public sealed class UiStackSystem() : UiVersionedSystemBase(
    Matchers.Of<Node, ComputedNode, UiRoot>())
{
    private readonly HashSet<Entity> _visited = [];

    protected override long GetVersion(UiChangeTracker changes) => changes.LayoutVersion;

    protected override void OnExecute(World world, IEntityQuery query)
    {
        var counter = 0;
        _visited.Clear();
        foreach (var root in query) {
            Visit(root, ref counter, _visited);
        }
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

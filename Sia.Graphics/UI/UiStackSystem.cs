using Sia;

namespace Sia.Graphics.UI;

public sealed class UiStackSystem() : SystemBase(Matchers.Of<Node, ComputedNode, UiRoot>())
{
    public override void Execute(World world, IEntityQuery query)
    {
        var counter = 0;
        var visited = new HashSet<Entity>();
        foreach (var root in query) {
            Visit(root, ref counter, visited);
        }
    }

    private static void Visit(Entity entity, ref int counter, HashSet<Entity> visited)
    {
        if (!visited.Add(entity))
            return;
        entity.Get<ComputedNode>().StackIndex = counter++;

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

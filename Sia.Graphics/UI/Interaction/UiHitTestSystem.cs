using Sia;

namespace Sia.Graphics.UI;

public sealed class UiHitTestSystem() : SystemBase(
    Matchers.Of<Node, ComputedNode, UiGlobalTransform, UiRoot>())
{
    public override void Execute(World world, IEntityQuery query)
    {
        var pointer = world.AcquireAddon<UiPointerState>();
        var state = world.AcquireAddon<UiInteractionState>();

        var hit = FindTopmostHit(query, pointer.Position);
        var delta = new Point(pointer.Position.X - state.LastPosition.X, pointer.Position.Y - state.LastPosition.Y);

        UpdateHover(world, state, hit);
        UpdatePressAndDrag(world, state, hit, pointer, delta);

        state.LastPosition = pointer.Position;
        state.WasButtonDown = pointer.ButtonDown;
    }

    private static void UpdateHover(World world, UiInteractionState state, Entity? hit)
    {
        if (Equals(hit, state.Hovered))
            return;

        if (state.Hovered is { IsValid: true } previous) {
            world.Dispatcher.Send(previous, new PointerLeave());
            if (previous.Contains<Hovered>())
                previous.Remove<Hovered>();
        }

        if (hit is { } current) {
            world.Dispatcher.Send(current, new PointerEnter());
            if (!current.Contains<Hovered>())
                current.Add<Hovered>();
        }

        state.Hovered = hit;
    }

    private static void UpdatePressAndDrag(
        World world, UiInteractionState state, Entity? hit, UiPointerState pointer, Point delta)
    {
        if (state.Pressed is { } captured && !captured.IsValid) {
            state.Pressed = null;
        }

        if (pointer.ButtonDown && !state.WasButtonDown && hit is { } pressTarget) {
            world.Dispatcher.Send(pressTarget, new PointerPress(0));
            if (!pressTarget.Contains<Pressed>())
                pressTarget.Add<Pressed>();
            world.Dispatcher.Send(pressTarget, new PointerDragStart(pointer.Position));
            state.Pressed = pressTarget;
            return;
        }

        if (pointer.ButtonDown && state.WasButtonDown && state.Pressed is { } dragging) {
            world.Dispatcher.Send(dragging, new PointerDrag(pointer.Position, delta));
            return;
        }

        if (!pointer.ButtonDown && state.WasButtonDown && state.Pressed is { } releasing) {
            world.Dispatcher.Send(releasing, new PointerRelease(0));
            if (releasing.Contains<Pressed>())
                releasing.Remove<Pressed>();
            world.Dispatcher.Send(releasing, new PointerDragEnd(pointer.Position));
            if (Equals(hit, releasing))
                world.Dispatcher.Send(releasing, new PointerClick(0));
            state.Pressed = null;
        }
    }

    private static Entity? FindTopmostHit(IEntityQuery roots, Point pointer)
    {
        var candidates = new List<Entity>();
        var visited = new HashSet<Entity>();
        foreach (var root in roots)
            CollectSubtree(root, candidates, visited);

        candidates.Sort((a, b) => b.Get<ComputedNode>().StackIndex.CompareTo(a.Get<ComputedNode>().StackIndex));

        foreach (var entity in candidates) {
            var computed = entity.Get<ComputedNode>();
            if (computed.ClipRect is { } clip && !clip.Contains(pointer))
                continue;
            var transform = entity.Get<UiGlobalTransform>();
            var local = transform.InverseTransform(pointer);
            if (computed.ContainsPoint(local))
                return entity;
        }

        return null;
    }

    private static void CollectSubtree(
        Entity entity, List<Entity> result, HashSet<Entity> visited)
    {
        if (!visited.Add(entity))
            return;
        result.Add(entity);
        if (!entity.Contains<UiChildren>())
            return;

        foreach (var child in entity.Get<UiChildren>().Value) {
            if (child.IsValid && child.Contains<Node>() && child.Contains<ComputedNode>())
                CollectSubtree(child, result, visited);
        }
    }
}

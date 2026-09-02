using Sia;

namespace Sia.Graphics.UI;

public sealed class UiHitTestSystem() : SystemBase(
    Matchers.Of<Node, ComputedNode, UiGlobalTransform, UiRoot>())
{
    public override void Execute(WorldContext context, IEntityQuery query)
    {
        var world = context.World;
        var pointer = world.AcquireAddon<UiPointerState>();
        var state = world.AcquireAddon<UiInteractionState>();

        var processed = false;
        while (pointer.TryRead(out var position, out var buttonDown)) {
            ProcessPointer(world, query, state, position, buttonDown);
            processed = true;
        }

        if (!processed)
            ProcessPointer(world, query, state, pointer.Position, pointer.ButtonDown);
    }

    private static void ProcessPointer(
        World world,
        IEntityQuery query,
        UiInteractionState state,
        Point position,
        bool buttonDown)
    {
        var hit = FindTopmostHit(query, position);
        var delta = new Point(
            position.X - state.LastPosition.X,
            position.Y - state.LastPosition.Y);

        UpdateHover(world, state, hit);
        UpdatePressAndDrag(world, state, hit, position, buttonDown, delta);

        state.LastPosition = position;
        state.WasButtonDown = buttonDown;
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
        World world,
        UiInteractionState state,
        Entity? hit,
        Point position,
        bool buttonDown,
        Point delta)
    {
        if (state.Pressed is { } captured && !captured.IsValid) {
            state.Pressed = null;
        }

        if (buttonDown && !state.WasButtonDown && hit is { } pressTarget) {
            world.Dispatcher.Send(pressTarget, new PointerPress(0));
            if (!pressTarget.Contains<Pressed>())
                pressTarget.Add<Pressed>();
            world.Dispatcher.Send(pressTarget, new PointerDragStart(position));
            state.Pressed = pressTarget;
            return;
        }

        if (buttonDown && state.WasButtonDown && state.Pressed is { } dragging) {
            world.Dispatcher.Send(dragging, new PointerDrag(position, delta));
            return;
        }

        if (!buttonDown && state.WasButtonDown && state.Pressed is { } releasing) {
            world.Dispatcher.Send(releasing, new PointerRelease(0));
            if (releasing.Contains<Pressed>())
                releasing.Remove<Pressed>();
            world.Dispatcher.Send(releasing, new PointerDragEnd(position));
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

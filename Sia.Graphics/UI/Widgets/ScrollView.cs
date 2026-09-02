using Sia;

namespace Sia.Graphics.UI;

public readonly record struct ScrollView
{
    public bool Horizontal { get; init; } = false;
    public bool Vertical { get; init; } = true;
    public float WheelStep { get; init; } = 32f;

    public ScrollView() { }

    public ScrollView(bool horizontal, bool vertical = true, float wheelStep = 32f)
    {
        Horizontal = horizontal;
        Vertical = vertical;
        WheelStep = wheelStep;
    }
}

public readonly record struct Scrolled(Point Position) : IEvent;

public sealed class ScrollViewSystem() : SystemBase(Matchers.Of<ScrollView, ComputedNode>())
{
    public override void Execute(WorldContext context, IEntityQuery query)
    {
        _ = query;
        var world = context.World;
        var delta = world.AcquireAddon<UiInputState>().ConsumeScrollDelta();
        if (delta == default)
            return;

        var interaction = world.AcquireAddon<UiInteractionState>();
        var target = FindScrollView(interaction.Hovered);
        if (target is null)
            return;

        var entity = target.Value;
        var view = entity.Get<ScrollView>();
        var computed = entity.Get<ComputedNode>();
        var x = view.Horizontal
            ? computed.ScrollPosition.X - (float)delta.X * view.WheelStep
            : computed.ScrollPosition.X;
        var y = view.Vertical
            ? computed.ScrollPosition.Y - (float)delta.Y * view.WheelStep
            : computed.ScrollPosition.Y;
        var position = new Point(
            System.Math.Clamp(x, 0f, computed.ScrollExtent.Width),
            System.Math.Clamp(y, 0f, computed.ScrollExtent.Height));
        if (position == computed.ScrollPosition)
            return;

        computed.ScrollPosition = position;
        entity.Set(computed);
        world.AcquireAddon<UiChangeTracker>().MarkLayoutDirty();
        world.Dispatcher.Send(entity, new Scrolled(position));
    }

    private static Entity? FindScrollView(Entity? start)
    {
        var current = start;
        while (current is { IsValid: true } entity) {
            if (entity.Contains<ScrollView>() && entity.Contains<ComputedNode>()
                && !entity.Contains<Disabled>()) {
                return entity;
            }
            current = entity.Contains<UiChildOf>()
                ? entity.Get<UiChildOf>().Parent
                : null;
        }
        return null;
    }
}

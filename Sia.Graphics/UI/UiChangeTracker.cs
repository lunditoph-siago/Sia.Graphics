using Sia;

namespace Sia.Graphics.UI;

public sealed class UiChangeTracker : IAddon
{
    private readonly HashSet<Entity> _renderDirtyEntities = [];

    public long HierarchyVersion { get; private set; } = 1;
    public long LayoutVersion { get; private set; } = 1;
    public long RenderVersion { get; private set; } = 1;
    internal long RenderStructureVersion { get; private set; } = 1;

    public void MarkHierarchyDirty()
    {
        HierarchyVersion++;
        MarkLayoutDirty();
    }

    public void MarkLayoutDirty()
    {
        LayoutVersion++;
        RenderVersion++;
        RenderStructureVersion++;
    }

    public void MarkRenderDirty()
    {
        RenderVersion++;
        RenderStructureVersion++;
    }

    void IAddon.OnInitialize(World world)
    {
        Subscribe<UiNodeIdentity, HierarchyInvalidation>(world);

        Subscribe<Node, LayoutInvalidation>(world);
        Subscribe<UiRoot, LayoutInvalidation>(world);
        Subscribe<UiChildren, LayoutInvalidation>(world);
        Subscribe<UiChildOf, LayoutInvalidation>(world);
        Subscribe<ZIndex, LayoutInvalidation>(world);
        Subscribe<Text, LayoutInvalidation>(world);
        Subscribe<TextStyle, LayoutInvalidation>(world);

        SubscribeRender<ComputedNode>(world);
        SubscribeRender<UiGlobalTransform>(world);
        SubscribeRender<BackgroundColor>(world);
        SubscribeRender<BorderColor>(world);
        SubscribeRender<TextLayoutInfo>(world);
    }

    void IAddon.OnUninitialize(World world)
    {
        Unsubscribe<UiNodeIdentity, HierarchyInvalidation>(world);

        Unsubscribe<Node, LayoutInvalidation>(world);
        Unsubscribe<UiRoot, LayoutInvalidation>(world);
        Unsubscribe<UiChildren, LayoutInvalidation>(world);
        Unsubscribe<UiChildOf, LayoutInvalidation>(world);
        Unsubscribe<ZIndex, LayoutInvalidation>(world);
        Unsubscribe<Text, LayoutInvalidation>(world);
        Unsubscribe<TextStyle, LayoutInvalidation>(world);

        UnsubscribeRender<ComputedNode>(world);
        UnsubscribeRender<UiGlobalTransform>(world);
        UnsubscribeRender<BackgroundColor>(world);
        UnsubscribeRender<BorderColor>(world);
        UnsubscribeRender<TextLayoutInfo>(world);
        _renderDirtyEntities.Clear();
    }

    private void Subscribe<TComponent, TInvalidation>(World world)
        where TInvalidation : IInvalidation
    {
        world.Dispatcher.Listen<WorldEvents.Add<TComponent>>(
            OnChanged<TInvalidation, WorldEvents.Add<TComponent>>);
        world.Dispatcher.Listen<WorldEvents.Set<TComponent>>(
            OnChanged<TInvalidation, WorldEvents.Set<TComponent>>);
        world.Dispatcher.Listen<WorldEvents.Remove<TComponent>>(
            OnChanged<TInvalidation, WorldEvents.Remove<TComponent>>);
    }

    private void Unsubscribe<TComponent, TInvalidation>(World world)
        where TInvalidation : IInvalidation
    {
        world.Dispatcher.Unlisten<WorldEvents.Add<TComponent>>(
            OnChanged<TInvalidation, WorldEvents.Add<TComponent>>);
        world.Dispatcher.Unlisten<WorldEvents.Set<TComponent>>(
            OnChanged<TInvalidation, WorldEvents.Set<TComponent>>);
        world.Dispatcher.Unlisten<WorldEvents.Remove<TComponent>>(
            OnChanged<TInvalidation, WorldEvents.Remove<TComponent>>);
    }

    private bool OnChanged<TInvalidation, TEvent>(Entity target, in TEvent @event)
        where TInvalidation : IInvalidation
        where TEvent : IEvent
    {
        _ = @event;
        TInvalidation.Apply(this, target);
        return false;
    }

    private void SubscribeRender<TComponent>(World world)
    {
        world.Dispatcher.Listen<WorldEvents.Add<TComponent>>(OnRenderStructureChanged);
        world.Dispatcher.Listen<WorldEvents.Remove<TComponent>>(OnRenderStructureChanged);
        world.Dispatcher.Listen<WorldEvents.Set<TComponent>>(OnRenderChanged);
    }

    private void UnsubscribeRender<TComponent>(World world)
    {
        world.Dispatcher.Unlisten<WorldEvents.Add<TComponent>>(OnRenderStructureChanged);
        world.Dispatcher.Unlisten<WorldEvents.Remove<TComponent>>(OnRenderStructureChanged);
        world.Dispatcher.Unlisten<WorldEvents.Set<TComponent>>(OnRenderChanged);
    }

    private bool OnRenderChanged<TEvent>(Entity target, in TEvent @event)
        where TEvent : IEvent
    {
        _ = @event;
        RenderVersion++;
        _renderDirtyEntities.Add(target);
        return false;
    }

    private bool OnRenderStructureChanged<TEvent>(Entity target, in TEvent @event)
        where TEvent : IEvent
    {
        _ = @event;
        RenderVersion++;
        RenderStructureVersion++;
        _renderDirtyEntities.Add(target);
        return false;
    }

    internal void ConsumeRenderDirtyEntities(List<Entity> destination)
    {
        destination.Clear();
        destination.AddRange(_renderDirtyEntities);
        _renderDirtyEntities.Clear();
    }

    private interface IInvalidation
    {
        static abstract void Apply(UiChangeTracker tracker, Entity target);
    }

    private readonly struct HierarchyInvalidation : IInvalidation
    {
        public static void Apply(UiChangeTracker tracker, Entity target)
        {
            _ = target;
            tracker.MarkHierarchyDirty();
        }
    }

    private readonly struct LayoutInvalidation : IInvalidation
    {
        public static void Apply(UiChangeTracker tracker, Entity target)
        {
            _ = target;
            tracker.MarkLayoutDirty();
        }
    }
}

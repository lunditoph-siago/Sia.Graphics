using Sia;

namespace Sia.Graphics.UI;

public sealed class UiChangeTracker : IAddon
{
    public long HierarchyVersion { get; private set; } = 1;
    public long LayoutVersion { get; private set; } = 1;
    public long RenderVersion { get; private set; } = 1;

    public void MarkHierarchyDirty()
    {
        HierarchyVersion++;
        MarkLayoutDirty();
    }

    public void MarkLayoutDirty()
    {
        LayoutVersion++;
        RenderVersion++;
    }

    public void MarkRenderDirty() => RenderVersion++;

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

        Subscribe<ComputedNode, RenderInvalidation>(world);
        Subscribe<UiGlobalTransform, RenderInvalidation>(world);
        Subscribe<BackgroundColor, RenderInvalidation>(world);
        Subscribe<BorderColor, RenderInvalidation>(world);
        Subscribe<TextLayoutInfo, RenderInvalidation>(world);
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

        Unsubscribe<ComputedNode, RenderInvalidation>(world);
        Unsubscribe<UiGlobalTransform, RenderInvalidation>(world);
        Unsubscribe<BackgroundColor, RenderInvalidation>(world);
        Unsubscribe<BorderColor, RenderInvalidation>(world);
        Unsubscribe<TextLayoutInfo, RenderInvalidation>(world);
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
        _ = target;
        _ = @event;
        TInvalidation.Apply(this);
        return false;
    }

    private interface IInvalidation
    {
        static abstract void Apply(UiChangeTracker tracker);
    }

    private readonly struct HierarchyInvalidation : IInvalidation
    {
        public static void Apply(UiChangeTracker tracker) => tracker.MarkHierarchyDirty();
    }

    private readonly struct LayoutInvalidation : IInvalidation
    {
        public static void Apply(UiChangeTracker tracker) => tracker.MarkLayoutDirty();
    }

    private readonly struct RenderInvalidation : IInvalidation
    {
        public static void Apply(UiChangeTracker tracker) => tracker.MarkRenderDirty();
    }
}

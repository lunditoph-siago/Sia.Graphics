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
        ListenHierarchy<UiNodeKey>(world);
        ListenHierarchy<UiParentKey>(world);
        ListenHierarchy<UiSiblingOrder>(world);

        ListenLayout<Node>(world);
        ListenLayout<UiRoot>(world);
        ListenLayout<UiChildren>(world);
        ListenLayout<UiChildOf>(world);
        ListenLayout<ZIndex>(world);
        ListenLayout<Text>(world);
        ListenLayout<TextStyle>(world);

        ListenRender<ComputedNode>(world);
        ListenRender<UiGlobalTransform>(world);
        ListenRender<BackgroundColor>(world);
        ListenRender<BorderColor>(world);
        ListenRender<TextLayoutInfo>(world);
    }

    void IAddon.OnUninitialize(World world)
    {
        UnlistenHierarchy<UiNodeKey>(world);
        UnlistenHierarchy<UiParentKey>(world);
        UnlistenHierarchy<UiSiblingOrder>(world);

        UnlistenLayout<Node>(world);
        UnlistenLayout<UiRoot>(world);
        UnlistenLayout<UiChildren>(world);
        UnlistenLayout<UiChildOf>(world);
        UnlistenLayout<ZIndex>(world);
        UnlistenLayout<Text>(world);
        UnlistenLayout<TextStyle>(world);

        UnlistenRender<ComputedNode>(world);
        UnlistenRender<UiGlobalTransform>(world);
        UnlistenRender<BackgroundColor>(world);
        UnlistenRender<BorderColor>(world);
        UnlistenRender<TextLayoutInfo>(world);
    }

    private void ListenHierarchy<TComponent>(World world)
    {
        world.Dispatcher.Listen<WorldEvents.Add<TComponent>>(OnHierarchyChanged);
        world.Dispatcher.Listen<WorldEvents.Set<TComponent>>(OnHierarchyChanged);
        world.Dispatcher.Listen<WorldEvents.Remove<TComponent>>(OnHierarchyChanged);
    }

    private void UnlistenHierarchy<TComponent>(World world)
    {
        world.Dispatcher.Unlisten<WorldEvents.Add<TComponent>>(OnHierarchyChanged);
        world.Dispatcher.Unlisten<WorldEvents.Set<TComponent>>(OnHierarchyChanged);
        world.Dispatcher.Unlisten<WorldEvents.Remove<TComponent>>(OnHierarchyChanged);
    }

    private void ListenLayout<TComponent>(World world)
    {
        world.Dispatcher.Listen<WorldEvents.Add<TComponent>>(OnLayoutChanged);
        world.Dispatcher.Listen<WorldEvents.Set<TComponent>>(OnLayoutChanged);
        world.Dispatcher.Listen<WorldEvents.Remove<TComponent>>(OnLayoutChanged);
    }

    private void UnlistenLayout<TComponent>(World world)
    {
        world.Dispatcher.Unlisten<WorldEvents.Add<TComponent>>(OnLayoutChanged);
        world.Dispatcher.Unlisten<WorldEvents.Set<TComponent>>(OnLayoutChanged);
        world.Dispatcher.Unlisten<WorldEvents.Remove<TComponent>>(OnLayoutChanged);
    }

    private void ListenRender<TComponent>(World world)
    {
        world.Dispatcher.Listen<WorldEvents.Add<TComponent>>(OnRenderChanged);
        world.Dispatcher.Listen<WorldEvents.Set<TComponent>>(OnRenderChanged);
        world.Dispatcher.Listen<WorldEvents.Remove<TComponent>>(OnRenderChanged);
    }

    private void UnlistenRender<TComponent>(World world)
    {
        world.Dispatcher.Unlisten<WorldEvents.Add<TComponent>>(OnRenderChanged);
        world.Dispatcher.Unlisten<WorldEvents.Set<TComponent>>(OnRenderChanged);
        world.Dispatcher.Unlisten<WorldEvents.Remove<TComponent>>(OnRenderChanged);
    }

    private bool OnLayoutChanged<TEvent>(Entity target, in TEvent @event)
        where TEvent : IEvent
    {
        _ = target;
        _ = @event;
        MarkLayoutDirty();
        return false;
    }

    private bool OnHierarchyChanged<TEvent>(Entity target, in TEvent @event)
        where TEvent : IEvent
    {
        _ = target;
        _ = @event;
        MarkHierarchyDirty();
        return false;
    }

    private bool OnRenderChanged<TEvent>(Entity target, in TEvent @event)
        where TEvent : IEvent
    {
        _ = target;
        _ = @event;
        MarkRenderDirty();
        return false;
    }
}

using Sia;

namespace Sia.WebGPU;

public sealed class WgpuResources : IAddon
{
    private interface IOwnedResource
    {
        public Type ComponentType { get; }

        public void Release();
    }

    private sealed class OwnedResource<T>(
        WgpuHandle<T> handle,
        WgpuReleaseHandler<T> release) : IOwnedResource
        where T : unmanaged
    {
        private WgpuHandle<T> _handle = handle;
        private WgpuReleaseHandler<T>? _release = release;

        public Type ComponentType => typeof(WgpuResource<T>);

        public void Release()
        {
            var release = _release;
            if (release is null) {
                return;
            }

            _release = null;
            release(ref _handle);
            _handle = default;
        }
    }

    private sealed class EntityResources
    {
        public List<IOwnedResource> Resources { get; } = [];
    }

    private readonly Dictionary<EntityId, EntityResources> _entities = [];
    private readonly Dictionary<Type, Action<World>> _componentUnlisteners = [];
    private WorldDispatcher.Listener<WorldEvents.Remove>? _entityRemoved;
    private World? _world;

    public int Count { get; private set; }

    public void OnInitialize(World world)
    {
        _world = world;
        _entityRemoved = OnEntityRemoved;
        world.Dispatcher.Listen(_entityRemoved);
    }

    public void OnUninitialize(World world)
    {
        List<Exception>? errors = null;
        var groups = _entities.Values.ToArray();
        _entities.Clear();
        Count = 0;

        if (_entityRemoved is not null) {
            world.Dispatcher.Unlisten(_entityRemoved);
            _entityRemoved = null;
        }
        foreach (var unlisten in _componentUnlisteners.Values) {
            unlisten(world);
        }
        _componentUnlisteners.Clear();

        foreach (var group in groups) {
            ReleaseAll(group, ref errors);
        }

        _world = null;
        if (errors is not null) {
            throw new AggregateException(
                "One or more WebGPU resources could not be released.", errors);
        }
    }

    internal void Own<T>(
        Entity entity,
        WgpuHandle<T> handle,
        WgpuReleaseHandler<T> release)
        where T : unmanaged
    {
        if (_world is null) {
            throw new InvalidOperationException(
                "The WebGPU resource module is not attached to a world.");
        }
        if (!entity.IsValid || !entity.Contains<WgpuResource<T>>()) {
            throw new ArgumentException(
                $"The entity does not contain {typeof(WgpuResource<T>).Name}.",
                nameof(entity));
        }
        WgpuWorldExtensions.ValidateWgpuTarget(_world, entity);
        if (handle.IsNull) {
            throw new ArgumentException("A null WebGPU handle cannot be owned.", nameof(handle));
        }
        ArgumentNullException.ThrowIfNull(release);

        if (!_entities.TryGetValue(entity.Id, out var group)) {
            group = new EntityResources();
            _entities.Add(entity.Id, group);
        }

        if (group.Resources.Any(
            resource => resource.ComponentType == typeof(WgpuResource<T>))) {
            throw new InvalidOperationException(
                $"Entity {entity.Id} already owns {typeof(T).Name}.");
        }

        EnsureComponentListener<T>();
        group.Resources.Add(new OwnedResource<T>(handle, release));
        Count++;
    }

    private void EnsureComponentListener<T>()
        where T : unmanaged
    {
        if (_componentUnlisteners.ContainsKey(typeof(T))) {
            return;
        }

        WorldDispatcher.Listener<WorldEvents.Remove<WgpuResource<T>>> listener =
            OnComponentRemoved;
        _world!.Dispatcher.Listen(listener);
        _componentUnlisteners.Add(
            typeof(T), world => world.Dispatcher.Unlisten(listener));
    }

    internal bool Owns<T>(Entity entity)
        where T : unmanaged =>
        _entities.TryGetValue(entity.Id, out var group)
        && group.Resources.Any(static resource => resource.ComponentType == typeof(WgpuResource<T>));

    private bool OnEntityRemoved(Entity target, in WorldEvents.Remove @event)
    {
        ReleaseAll(target.Id);
        return false;
    }

    private bool OnComponentRemoved<T>(
        Entity target,
        in WorldEvents.Remove<WgpuResource<T>> @event)
        where T : unmanaged
    {
        if (!target.IsValid || !target.Contains<WgpuResource<T>>()) {
            Release(target.Id, typeof(WgpuResource<T>));
        }

        return false;
    }

    private void Release(EntityId entityId, Type componentType)
    {
        if (!_entities.TryGetValue(entityId, out var group)) {
            return;
        }

        for (var index = group.Resources.Count - 1; index >= 0; index--) {
            var resource = group.Resources[index];
            if (resource.ComponentType != componentType) {
                continue;
            }

            group.Resources.RemoveAt(index);
            Count--;
            if (group.Resources.Count == 0) {
                _entities.Remove(entityId);
            }
            resource.Release();
            return;
        }
    }

    private void ReleaseAll(EntityId entityId)
    {
        if (!_entities.Remove(entityId, out var group)) {
            return;
        }

        Count -= group.Resources.Count;
        List<Exception>? errors = null;
        ReleaseAll(group, ref errors);
        if (errors is not null) {
            throw new AggregateException(
                "One or more WebGPU resources could not be released.", errors);
        }
    }

    private static void ReleaseAll(
        EntityResources group,
        ref List<Exception>? errors)
    {
        for (var index = group.Resources.Count - 1; index >= 0; index--) {
            try {
                group.Resources[index].Release();
            }
            catch (Exception exception) {
                (errors ??= []).Add(exception);
            }
        }
        group.Resources.Clear();
    }
}

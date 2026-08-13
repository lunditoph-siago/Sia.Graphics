using Sia;

namespace Sia.Graphics.UI;

public sealed class UiHierarchySystem() : UiVersionedSystemBase(
    Matchers.Of<UiNodeIdentity>())
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Entity> _entities = new(StringComparer.Ordinal);

    protected override long GetVersion(UiChangeTracker changes) => changes.HierarchyVersion;

    protected override void OnExecute(World world, IEntityQuery query)
    {
        _entries.Clear();
        _entries.EnsureCapacity(query.Count);
        query.ForEach(
            _entries,
            static (in List<Entry> output, Entity entity) => {
                var identity = entity.Get<UiNodeIdentity>();
                output.Add(new(
                    entity,
                    identity.Key,
                    identity.ParentKey,
                    identity.SiblingOrder));
            });

        _entities.Clear();
        _entities.EnsureCapacity(_entries.Count);
        foreach (var entry in _entries) {
            if (!_entities.TryAdd(entry.Key, entry.Entity)) {
                throw new InvalidOperationException(
                    $"Reactive UI node key '{entry.Key}' is duplicated.");
            }
        }

        var children = new Dictionary<Entity, List<Entry>>();
        foreach (var entry in _entries) {
            EnsureOutputs(entry.Entity);
            if (entry.ParentKey is null)
                continue;
            if (!_entities.TryGetValue(entry.ParentKey, out var parent)) {
                throw new InvalidOperationException(
                    $"Reactive UI parent key '{entry.ParentKey}' for '{entry.Key}' was not found.");
            }
            if (!children.TryGetValue(parent, out var siblings)) {
                siblings = [];
                children.Add(parent, siblings);
            }
            siblings.Add(entry);
            SetParent(entry.Entity, parent);
        }

        foreach (var entry in _entries) {
            if (entry.ParentKey is null && entry.Entity.Contains<UiChildOf>())
                entry.Entity.Remove<UiChildOf>();

            var next = children.TryGetValue(entry.Entity, out var siblings)
                ? siblings
                    .OrderBy(static child => child.Order)
                    .ThenBy(static child => child.Key, StringComparer.Ordinal)
                    .Select(static child => child.Entity)
                    .ToList()
                : [];
            SetChildren(entry.Entity, next);
        }

    }

    private static void EnsureOutputs(Entity entity)
    {
        if (!entity.Contains<ComputedNode>())
            entity.Add(new ComputedNode());
        if (!entity.Contains<UiGlobalTransform>())
            entity.Add(UiGlobalTransform.Identity);
        if (entity.Contains<Text>() && entity.Contains<TextStyle>()
            && !entity.Contains<TextLayoutInfo>())
            entity.Add(new TextLayoutInfo());
    }

    private static void SetParent(Entity entity, Entity parent)
    {
        var relation = new UiChildOf(parent);
        if (!entity.Contains<UiChildOf>())
            entity.Add(relation);
        else if (entity.Get<UiChildOf>() != relation)
            entity.Set(relation);
    }

    private static void SetChildren(Entity entity, List<Entity> children)
    {
        if (!entity.Contains<UiChildren>()) {
            entity.Add(new UiChildren { Value = children });
            return;
        }

        var current = entity.Get<UiChildren>().Value;
        if (!current.SequenceEqual(children))
            entity.Set(new UiChildren { Value = children });
    }

    private readonly record struct Entry(
        Entity Entity,
        string Key,
        string? ParentKey,
        int Order);
}

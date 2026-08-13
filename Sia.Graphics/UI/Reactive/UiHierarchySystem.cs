using Sia;

namespace Sia.Graphics.UI;

public sealed class UiHierarchySystem() : SystemBase(Matchers.Of<UiNodeKey, UiParentKey, UiSiblingOrder>())
{
    private long _preparedVersion = -1;

    public override void Execute(World world, IEntityQuery query)
    {
        var changes = world.AcquireAddon<UiChangeTracker>();
        if (_preparedVersion == changes.HierarchyVersion)
            return;

        var entries = new List<Entry>(query.Count);
        query.ForEach(
            entries,
            static (in List<Entry> output, Entity entity) =>
                output.Add(new(
                    entity,
                    entity.Get<UiNodeKey>().Value,
                    entity.Get<UiParentKey>().Value,
                    entity.Get<UiSiblingOrder>().Value)));

        var entities = new Dictionary<string, Entity>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries) {
            if (!entities.TryAdd(entry.Key, entry.Entity)) {
                throw new InvalidOperationException(
                    $"Reactive UI node key '{entry.Key}' is duplicated.");
            }
        }

        var children = new Dictionary<Entity, List<Entry>>();
        foreach (var entry in entries) {
            EnsureOutputs(entry.Entity);
            if (entry.ParentKey is null)
                continue;
            if (!entities.TryGetValue(entry.ParentKey, out var parent)) {
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

        foreach (var entry in entries) {
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

        _preparedVersion = changes.HierarchyVersion;
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

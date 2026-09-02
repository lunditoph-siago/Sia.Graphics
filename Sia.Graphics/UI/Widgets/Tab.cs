using Sia;

namespace Sia.Graphics.UI;

public readonly record struct Tab(string Group, string Value);

public readonly record struct TabSelected(string Group, string Value) : IEvent;

public sealed class TabSystem() : SystemBase(Matchers.Of<Tab>())
{
    private IEntityQuery? _query;

    public override void Initialize(World world)
    {
        _query = world.Query(Matchers.Of<Tab>());

        world.Dispatcher.Listen<PointerClick>((Entity target, in PointerClick _) => {
            if (!target.IsValid || !target.Contains<Tab>()
                || target.Contains<Disabled>() || target.Contains<Selected>()) {
                return false;
            }

            var selected = target.Get<Tab>();
            Span<Entity> members = new Entity[_query!.Count];
            _query.Record(members);

            foreach (var member in members) {
                if (member != target && member.IsValid && member.Contains<Selected>()
                    && member.Get<Tab>().Group == selected.Group) {
                    member.Remove<Selected>();
                }
            }

            target.Add<Selected>();
            world.Dispatcher.Send(target, new TabSelected(selected.Group, selected.Value));
            return false;
        });
    }
}

using Sia;

namespace Sia.Graphics.UI;

public readonly record struct Tab(string Group, string Value);

public readonly record struct TabSelected(string Group, string Value) : IEvent;

public sealed class TabSystem() : SystemBase(Matchers.Of<Tab>())
{
    public override void Initialize(World world)
    {
        world.Dispatcher.Listen<PointerClick>((Entity target, in PointerClick _) => {
            if (!target.IsValid || !target.Contains<Tab>()
                || target.Contains<Disabled>() || target.Contains<Selected>()) {
                return false;
            }

            var selected = target.Get<Tab>();
            var members = new List<Entity>();
            world.Query(
                Matchers.Of<Tab>(),
                members,
                static (in List<Entity> output, Entity entity) => output.Add(entity));
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

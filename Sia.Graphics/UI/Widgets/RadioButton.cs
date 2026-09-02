using Sia;

namespace Sia.Graphics.UI;

public readonly record struct RadioButton(string Group, string Value);

public readonly record struct RadioSelected(string Group, string Value) : IEvent;

public sealed class RadioButtonSystem() : SystemBase(Matchers.Of<RadioButton>())
{
    private IEntityQuery? _query;

    public override void Initialize(World world)
    {
        _query = world.Query(Matchers.Of<RadioButton>());

        world.Dispatcher.Listen<PointerClick>((Entity target, in PointerClick _) => {
            if (!target.IsValid || !target.Contains<RadioButton>()
                || target.Contains<Disabled>() || target.Contains<Checked>()) {
                return false;
            }

            var selected = target.Get<RadioButton>();
            Span<Entity> members = new Entity[_query!.Count];
            _query.Record(members);

            foreach (var member in members) {
                if (!member.IsValid || member == target || !member.Contains<Checked>())
                    continue;
                if (member.Get<RadioButton>().Group == selected.Group)
                    member.Remove<Checked>();
            }

            target.Add<Checked>();
            world.Dispatcher.Send(target, new RadioSelected(selected.Group, selected.Value));
            return false;
        });
    }
}

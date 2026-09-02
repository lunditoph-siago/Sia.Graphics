using Sia;

namespace Sia.Graphics.UI;

public readonly record struct Dropdown(string Group);

public record struct DropdownValue(string Value);

public readonly record struct DropdownOption(string Group, string Value);

public readonly record struct DropdownExpandedChanged(bool Expanded) : IEvent;

public readonly record struct DropdownChanged(string Value) : IEvent;

public sealed class DropdownSystem() : SystemBase(
    Matchers.Of<Dropdown>().Or(Matchers.Of<DropdownOption>()))
{
    private IEntityQuery? _dropdowns;
    private IEntityQuery? _expanded;

    public override void Initialize(World world)
    {
        _dropdowns = world.Query(Matchers.Of<Dropdown, DropdownValue>());
        _expanded = world.Query(Matchers.Of<Dropdown, Expanded>());

        world.Dispatcher.Listen<PointerClick>((Entity target, in PointerClick _) => {
            if (!target.IsValid || target.Contains<Disabled>()) {
                CloseAll(world, null);
                return false;
            }

            if (target.Contains<Dropdown>() && target.Contains<DropdownValue>()) {
                var expanded = !target.Contains<Expanded>();
                CloseAll(world, expanded ? target : null);
                if (expanded)
                    target.Add<Expanded>();
                world.Dispatcher.Send(target, new DropdownExpandedChanged(expanded));
                return false;
            }

            if (target.Contains<DropdownOption>()) {
                var option = target.Get<DropdownOption>();
                var owner = FindDropdown(option.Group);
                if (owner is { } dropdown) {
                    dropdown.Set(new DropdownValue(option.Value));
                    if (dropdown.Contains<Expanded>())
                        dropdown.Remove<Expanded>();
                    world.Dispatcher.Send(dropdown, new DropdownChanged(option.Value));
                    world.Dispatcher.Send(dropdown, new DropdownExpandedChanged(false));
                }
                CloseAll(world, null);
                return false;
            }

            CloseAll(world, null);
            return false;
        });
    }

    private Entity? FindDropdown(string group)
    {
        Span<Entity> dropdowns = new Entity[_dropdowns!.Count];
        _dropdowns.Record(dropdowns);
        foreach (var entity in dropdowns) {
            if (entity.Get<Dropdown>().Group == group)
                return entity;
        }
        return null;
    }

    private void CloseAll(World world, Entity? except)
    {
        Span<Entity> expanded = new Entity[_expanded!.Count];
        _expanded.Record(expanded);
        foreach (var dropdown in expanded) {
            if (dropdown == except)
                continue;
            dropdown.Remove<Expanded>();
            world.Dispatcher.Send(dropdown, new DropdownExpandedChanged(false));
        }
    }
}

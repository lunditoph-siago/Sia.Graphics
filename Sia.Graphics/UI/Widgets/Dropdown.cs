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
    public override void Initialize(World world)
    {
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
                var owner = FindDropdown(world, option.Group);
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

    private static Entity? FindDropdown(World world, string group)
    {
        Entity? result = null;
        world.Query(Matchers.Of<Dropdown, DropdownValue>(), (Entity entity) => {
            if (result is null && entity.Get<Dropdown>().Group == group)
                result = entity;
        });
        return result;
    }

    private static void CloseAll(World world, Entity? except)
    {
        var expanded = new List<Entity>();
        world.Query(
            Matchers.Of<Dropdown, Expanded>(),
            expanded,
            static (in List<Entity> output, Entity entity) => output.Add(entity));
        foreach (var dropdown in expanded) {
            if (dropdown == except)
                continue;
            dropdown.Remove<Expanded>();
            world.Dispatcher.Send(dropdown, new DropdownExpandedChanged(false));
        }
    }
}

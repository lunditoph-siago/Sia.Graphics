using Sia;

namespace Sia.Graphics.UI;

public record struct Checkbox;

public readonly record struct CheckedChanged(bool Value) : IEvent;

public sealed class CheckboxSystem() : SystemBase(Matchers.Of<Checkbox>())
{
    public override void Initialize(World world)
    {
        world.Dispatcher.Listen<PointerClick>((Entity target, in PointerClick e) => {
            if (!target.IsValid || !target.Contains<Checkbox>() || target.Contains<Disabled>())
                return false;

            if (target.Contains<Checked>())
                target.Remove<Checked>();
            else
                target.Add<Checked>();

            world.Dispatcher.Send(target, new CheckedChanged(target.Contains<Checked>()));
            return false;
        });
    }
}

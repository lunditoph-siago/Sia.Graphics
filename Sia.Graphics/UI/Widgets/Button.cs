using Sia;

namespace Sia.Graphics.UI;

public record struct Button;

public readonly record struct Activate : IEvent;

public sealed class ButtonSystem() : SystemBase(Matchers.Of<Button>())
{
    public override void Initialize(World world)
    {
        world.Dispatcher.Listen<PointerClick>((Entity target, in PointerClick e) => {
            if (target.IsValid && target.Contains<Button>() && !target.Contains<Disabled>())
                world.Dispatcher.Send(target, new Activate());
            return false;
        });
    }
}

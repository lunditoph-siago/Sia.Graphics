using Sia;

namespace Sia.Graphics.UI;

public abstract class UiVersionedSystemBase(IEntityMatcher matcher) : SystemBase(matcher)
{
    private long _version = -1;

    protected abstract long GetVersion(UiChangeTracker changes);

    protected abstract void OnExecute(World world, IEntityQuery query);

    public sealed override void Execute(World world, IEntityQuery query)
    {
        var changes = world.AcquireAddon<UiChangeTracker>();
        if (_version == GetVersion(changes))
            return;

        OnExecute(world, query);
        _version = GetVersion(changes);
    }
}

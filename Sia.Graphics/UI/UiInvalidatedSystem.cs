using Sia;

namespace Sia.Graphics.UI;

public abstract class UiInvalidatedSystem(IEntityMatcher matcher) : SystemBase(matcher)
{
    private long _version = -1;

    protected abstract long GetVersion(UiChangeTracker changes);

    protected abstract void ExecuteInvalidated(World world, IEntityQuery query);

    public sealed override void Execute(World world, IEntityQuery query)
    {
        var changes = world.AcquireAddon<UiChangeTracker>();
        if (_version == GetVersion(changes))
            return;

        ExecuteInvalidated(world, query);
        _version = GetVersion(changes);
    }
}

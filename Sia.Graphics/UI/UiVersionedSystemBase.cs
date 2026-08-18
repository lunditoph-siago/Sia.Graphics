using Sia;

namespace Sia.Graphics.UI;

public abstract class UiVersionedSystemBase(IEntityMatcher matcher) : SystemBase(matcher)
{
    private long _version = -1;

    protected abstract long GetVersion(UiChangeTracker changes);

    protected abstract void OnExecute(World world, IEntityQuery query);

    public sealed override void Execute(WorldContext context, IEntityQuery query)
    {
        var changes = context.World.AcquireAddon<UiChangeTracker>();
        if (_version == GetVersion(changes))
            return;

        OnExecute(context.World, query);
        _version = GetVersion(changes);
    }
}

namespace Sia.Graphics.Reactive;

public sealed class RenderGraphRegistration : IDisposable
{
    private Action? _unregister;

    internal RenderGraphRegistration(Action unregister)
    {
        ArgumentNullException.ThrowIfNull(unregister);
        _unregister = unregister;
    }

    public bool IsRegistered => _unregister != null;

    public void Dispose() => Interlocked.Exchange(ref _unregister, null)?.Invoke();
}

namespace Sia.Graphics.Reactive;

public readonly record struct RenderGraphPassKey
{
    public RenderGraphPassKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

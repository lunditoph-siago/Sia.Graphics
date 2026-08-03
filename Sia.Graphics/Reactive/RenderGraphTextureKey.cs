namespace Sia.Graphics.Reactive;

public readonly record struct RenderGraphTextureKey
{
    public RenderGraphTextureKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

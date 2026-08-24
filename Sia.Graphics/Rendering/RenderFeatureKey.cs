namespace Sia.Graphics.Rendering;

public readonly record struct RenderFeatureKey
{
    public string Value { get; }

    public RenderFeatureKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public override string ToString() => Value;
}

namespace Sia.Graphics.Wgsl;

public enum WgslConditionalKind
{
    Ifdef,
    Ifndef,
    If,
    Else,
    Endif
}

public sealed class WgslConditionalDirective(
    WgslConditionalKind kind, string? name, string? op, string? value, int line)
{
    public WgslConditionalKind Kind { get; } = kind;
    public string? Name { get; } = name;
    public string? Op { get; } = op;
    public string? Value { get; } = value;
    public int Line { get; } = line;
}

namespace Sia.Graphics.Wgsl;

public sealed class WgslImportDirective(string path, string[]? items, string? alias, int line)
{
    public string Path { get; } = path;
    public string[]? Items { get; } = items;
    public string? Alias { get; } = alias;
    public int Line { get; } = line;
}

namespace Sia.Graphics.Wgsl;

public sealed class WgslModuleNode(string name, string source)
{
    public string Name { get; } = name;
    public string Source { get; } = source;
    public List<WgslModuleNode> Dependencies { get; } = [];
}

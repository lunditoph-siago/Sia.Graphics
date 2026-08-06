namespace Sia.Graphics.Wgsl;

public sealed class WgslModuleNode(string name, string source, WgslDirectiveInfo directives)
{
    public string Name { get; } = name;
    public string Source { get; } = source;
    public WgslDirectiveInfo Directives { get; } = directives;
    public List<WgslModuleNode> Dependencies { get; } = [];
}

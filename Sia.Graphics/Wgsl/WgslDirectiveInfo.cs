namespace Sia.Graphics.Wgsl;

public sealed class WgslDirectiveInfo(
    string? importPath,
    List<WgslImportDirective> imports,
    Dictionary<string, string> defines,
    List<WgslConditionalDirective> conditionals)
{
    public string? ImportPath { get; } = importPath;
    public List<WgslImportDirective> Imports { get; } = imports;
    public Dictionary<string, string> Defines { get; } = defines;
    public List<WgslConditionalDirective> Conditionals { get; } = conditionals;
}

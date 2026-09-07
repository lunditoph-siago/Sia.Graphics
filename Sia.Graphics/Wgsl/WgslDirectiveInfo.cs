namespace Sia.Graphics.Wgsl;

public sealed class WgslDirectiveInfo(
    string? importPath,
    List<WgslImportDirective> imports)
{
    public string? ImportPath { get; } = importPath;
    public List<WgslImportDirective> Imports { get; } = imports;
}

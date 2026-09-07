namespace Sia.Graphics.Wgsl;

public static class WgslDirectiveParser
{
    public static WgslDirectiveInfo Parse(string source)
    {
        var imports = new List<WgslImportDirective>();
        string? importPath = null;

        var lineNo = 0;
        var commentDepth = 0;
        var span = source.AsSpan();

        foreach (var rawLine in span.EnumerateLines()) {
            lineNo++;
            var trimmed = WgslCommentMask.Apply(rawLine, ref commentDepth).AsSpan().TrimStart();
            if (trimmed.IsEmpty) {
                continue;
            }

            if (trimmed[0] != '#') {
                continue;
            }

            ParseLine(trimmed, lineNo, imports, ref importPath);
        }

        return new WgslDirectiveInfo(importPath, imports);
    }

    private static void ParseLine(
        ReadOnlySpan<char> line,
        int lineNo,
        List<WgslImportDirective> imports,
        ref string? importPath)
    {
        if (line.StartsWith("#import ")) {
            var arg = line["#import ".Length..].TrimStart();
            var (path, items, alias) = ParseImportArg(arg);
            imports.Add(new WgslImportDirective(path, items, alias, lineNo));
        }
        else if (line.StartsWith("#define_import_path ")) {
            importPath = line["#define_import_path ".Length..].Trim().ToString();
        }
    }

    private static (string path, string[]? items, string? alias) ParseImportArg(ReadOnlySpan<char> arg)
    {
        string? alias = null;

        var asIdx = arg.IndexOf(" as ", StringComparison.Ordinal);
        if (asIdx >= 0) {
            alias = arg[(asIdx + 4)..].Trim().ToString();
            arg = arg[..asIdx].TrimEnd();
        }

        var braceIdx = arg.IndexOf('{');
        if (braceIdx < 0) {
            return (arg.ToString(), null, alias);
        }

        var closeIdx = arg.LastIndexOf('}');
        if (closeIdx < 0) {
            return (arg.ToString(), null, alias);
        }

        var path = arg[..braceIdx].TrimEnd().TrimEnd(':').ToString();
        var itemsStr = arg[(braceIdx + 1)..closeIdx];
        var items = itemsStr.ToString().Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        return (path, items, alias);
    }
}

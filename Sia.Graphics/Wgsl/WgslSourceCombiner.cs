using System.Text;

namespace Sia.Graphics.Wgsl;

public static class WgslSourceCombiner
{
    public static string Combine(
        IReadOnlyList<WgslModuleNode> modules)
    {
        var compiled = modules.Select(m => m.Source).ToList();

        var wgslDirectives = new List<string>();
        var seenDirectives = new HashSet<string>();
        foreach (var source in compiled) {
            ExtractWgslDirectives(source, wgslDirectives, seenDirectives);
        }

        var sb = new StringBuilder();

        foreach (var d in wgslDirectives) {
            sb.Append(d).Append('\n');
        }

        if (wgslDirectives.Count > 0) {
            sb.Append('\n');
        }

        for (var i = 0; i < modules.Count; i++) {
            if (i > 0) {
                sb.Append('\n');
            }

            sb.Append("// --- module: ").Append(modules[i].Name).Append(" ---\n");
            sb.Append('\n');

            var cleaned = StripWgslDirectives(compiled[i]);
            sb.Append(cleaned);

            if (!cleaned.EndsWith('\n')) {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static void ExtractWgslDirectives(
        string source,
        List<string> directives,
        HashSet<string> seen)
    {
        foreach (var rawLine in source.AsSpan().EnumerateLines()) {
            var line = rawLine.TrimStart();
            if (line.IsEmpty) {
                continue;
            }

            if (line.StartsWith("enable ") || line.StartsWith("requires ") || line.StartsWith("diagnostic ")) {
                var text = line.ToString();
                if (seen.Add(text)) {
                    directives.Add(text);
                }
            }
        }
    }

    private static string StripWgslDirectives(string source)
    {
        var sb = new StringBuilder(source.Length);
        foreach (var rawLine in source.AsSpan().EnumerateLines()) {
            var line = rawLine.TrimStart();
            if (!line.IsEmpty &&
                (line.StartsWith("enable ") || line.StartsWith("requires ") || line.StartsWith("diagnostic ") ||
                 line.StartsWith("#import ") || line.StartsWith("#define_import_path "))) {
                sb.Append('\n');
            }
            else {
                sb.Append(rawLine).Append('\n');
            }
        }
        return sb.ToString();
    }
}

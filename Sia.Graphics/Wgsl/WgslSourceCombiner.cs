using System.Text;

namespace Sia.Graphics.Wgsl;

public static class WgslSourceCombiner
{
    public static string Combine(
        IReadOnlyList<WgslModuleNode> modules,
        IReadOnlyDictionary<string, string>? shaderDefs,
        List<WgslDiagnostic>? diagnostics = null)
    {
        // Compile each module once
        var compiled = modules
            .Select(m => WgslConditionalCompiler.Compile(m.Source, shaderDefs, diagnostics))
            .ToList();

        // Extract WGSL enable/requires/diagnostic from all modules
        var wgslDirectives = new List<string>();
        var seenDirectives = new HashSet<string>();
        foreach (var source in compiled)
            ExtractWgslDirectives(source, wgslDirectives, seenDirectives);

        var sb = new StringBuilder();

        foreach (var d in wgslDirectives)
            sb.AppendLine(d);

        if (wgslDirectives.Count > 0)
            sb.AppendLine();

        for (var i = 0; i < modules.Count; i++) {
            if (i > 0)
                sb.AppendLine();

            sb.AppendLine($"// --- module: {modules[i].Name} ---");
            sb.AppendLine();

            var cleaned = StripWgslDirectives(compiled[i]);
            sb.Append(cleaned);

            if (!cleaned.EndsWith('\n'))
                sb.AppendLine();
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
            if (line.IsEmpty) continue;

            if (line.StartsWith("enable ") || line.StartsWith("requires ") || line.StartsWith("diagnostic ")) {
                var text = line.ToString();
                if (seen.Add(text))
                    directives.Add(text);
            }
        }
    }

    private static string StripWgslDirectives(string source)
    {
        var sb = new StringBuilder(source.Length);
        foreach (var rawLine in source.AsSpan().EnumerateLines()) {
            var line = rawLine.TrimStart();
            if (!line.IsEmpty &&
                (line.StartsWith("enable ") || line.StartsWith("requires ") || line.StartsWith("diagnostic "))) {
                sb.AppendLine();
            }
            else {
                sb.AppendLine(rawLine.ToString());
            }
        }
        return sb.ToString();
    }
}

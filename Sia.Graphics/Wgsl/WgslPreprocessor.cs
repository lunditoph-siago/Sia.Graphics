namespace Sia.Graphics.Wgsl;

public static class WgslPreprocessor
{
    public static WgslProcessResult Process(
        string source,
        IReadOnlyDictionary<string, string>? shaderDefs,
        WgslImportResolver importResolver)
    {
        var diagnostics = new List<WgslDiagnostic>();

        // Collect entry file's local defines and merge with external shaderDefs
        var entryDirectives = WgslDirectiveParser.Parse(source);
        var allDefs = MergeDefs(shaderDefs, entryDirectives.Defines);

        // Build module graph
        var modules = WgslModuleGraph.BuildAndSort("main", source, importResolver, diagnostics);
        if (diagnostics.Any(d => d.Severity == WgslDiagnosticSeverity.Error))
            return WgslProcessResult.Failure(diagnostics);

        // Combine into final WGSL
        var combined = WgslSourceCombiner.Combine(modules, allDefs, diagnostics);

        return new WgslProcessResult { CombinedSource = combined, Diagnostics = diagnostics };
    }

    public static WgslProcessResult ProcessFile(
        string filePath,
        IReadOnlyDictionary<string, string>? shaderDefs)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) {
            return WgslProcessResult.Failure(
            [
                new WgslDiagnostic(
                    WgslDiagnosticSeverity.Error,
                    $"File not found: '{filePath}'", 0, filePath)
            ]);
        }

        var source = File.ReadAllText(fullPath);
        var resolver = new WgslFileSystemImportResolver(Path.GetDirectoryName(fullPath)!, fullPath);

        return Process(source, shaderDefs, resolver.Resolve);
    }

    private static Dictionary<string, string> MergeDefs(
        IReadOnlyDictionary<string, string>? externalDefs,
        Dictionary<string, string> localDefs)
    {
        var merged = new Dictionary<string, string>();
        if (externalDefs != null) {
            foreach (var (k, v) in externalDefs)
                merged[k] = v;
        }
        foreach (var (k, v) in localDefs)
            merged[k] = v;
        return merged;
    }
}

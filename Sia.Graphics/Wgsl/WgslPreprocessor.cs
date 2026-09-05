namespace Sia.Graphics.Wgsl;

public static class WgslPreprocessor
{
    public static WgslProcessResult Process(
        string source,
        IReadOnlyDictionary<string, string>? shaderDefs,
        WgslImportResolver importResolver)
    {
        var diagnostics = new List<WgslDiagnostic>();

        var entrySource = WgslConditionalCompiler.CompileModule(source, shaderDefs, diagnostics, out var allDefs);
        var modules = WgslModuleGraph.BuildAndSort("main", entrySource, importResolver, diagnostics, allDefs);
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
}

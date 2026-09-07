namespace Sia.Graphics.Wgsl;

public static class WgslPreprocessor
{
    public static WgslProcessResult Process(
        string source,
        IReadOnlyDictionary<string, string>? shaderDefs,
        WgslImportResolver importResolver) =>
        ProcessWithContext(source, WgslCompilationContext.FromDefinitions(shaderDefs), importResolver);

    public static WgslProcessResult ProcessWithContext(
        string source,
        WgslCompilationContext? context,
        WgslImportResolver importResolver)
    {
        var diagnostics = new List<WgslDiagnostic>();

        var modules = WgslModuleGraph.BuildAndSort("main", source, importResolver, diagnostics, context);
        if (diagnostics.Any(d => d.Severity == WgslDiagnosticSeverity.Error)) {
            return WgslProcessResult.Failure(diagnostics);
        }

        var combined = WgslSourceCombiner.Combine(modules);

        return new WgslProcessResult {
            CombinedSource = combined,
            Diagnostics = diagnostics,
            ContextFingerprint = (context ?? WgslCompilationContext.Empty).Fingerprint
        };
    }

    public static WgslProcessResult ProcessFile(
        string filePath,
        IReadOnlyDictionary<string, string>? shaderDefs) =>
        ProcessFileWithContext(filePath, WgslCompilationContext.FromDefinitions(shaderDefs));

    public static WgslProcessResult ProcessFileWithContext(
        string filePath,
        WgslCompilationContext? context)
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

        return ProcessWithContext(source, context, resolver.Resolve);
    }
}

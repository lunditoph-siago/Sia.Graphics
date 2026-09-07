namespace Sia.Graphics.Wgsl;

public static class WgslModuleGraph
{
    public static List<WgslModuleNode> BuildAndSort(
        string entryName,
        string entrySource,
        WgslImportResolver importResolver,
        List<WgslDiagnostic> diagnostics,
        WgslCompilationContext? context = null)
    {
        var registry = new Dictionary<string, WgslModuleNode>();

        var entry = Build(entryName, entrySource, importResolver, registry, diagnostics, context);
        if (HasErrors(diagnostics)) {
            return [];
        }

        return TopologicalSort(entry, diagnostics);
    }

    private static WgslModuleNode Build(
        string name,
        string source,
        WgslImportResolver importResolver,
        Dictionary<string, WgslModuleNode> registry,
        List<WgslDiagnostic> diagnostics,
        WgslCompilationContext? context)
    {
        source = WgslConditionalCompiler.CompileModule(source, context, diagnostics, name);
        var directives = WgslDirectiveParser.Parse(source);
        var canonicalName = directives.ImportPath ?? name;

        if (registry.TryGetValue(canonicalName, out var existing)) {
            return existing;
        }

        var node = new WgslModuleNode(canonicalName, source);
        registry[canonicalName] = node;

        foreach (var imp in directives.Imports) {
            var resolvedSource = importResolver(imp.Path, node.Name);
            if (resolvedSource == null) {
                diagnostics.Add(new WgslDiagnostic(
                    WgslDiagnosticSeverity.Error,
                    $"Import not found: '{imp.Path}'",
                    imp.Line, name));
                continue;
            }

            var dep = Build(imp.Path, resolvedSource, importResolver, registry, diagnostics, context);
            if (!node.Dependencies.Contains(dep)) {
                node.Dependencies.Add(dep);
            }
        }

        return node;
    }

    private static List<WgslModuleNode> TopologicalSort(
        WgslModuleNode root,
        List<WgslDiagnostic> diagnostics)
    {
        var result = new List<WgslModuleNode>();
        var visited = new HashSet<WgslModuleNode>();
        var inStack = new HashSet<WgslModuleNode>();

        Visit(root, visited, inStack, result, diagnostics);

        return result;
    }

    private static void Visit(
        WgslModuleNode node,
        HashSet<WgslModuleNode> visited,
        HashSet<WgslModuleNode> inStack,
        List<WgslModuleNode> result,
        List<WgslDiagnostic> diagnostics)
    {
        if (visited.Contains(node)) {
            return;
        }

        if (inStack.Contains(node)) {
            diagnostics.Add(new WgslDiagnostic(
                WgslDiagnosticSeverity.Error,
                $"Circular dependency involving module '{node.Name}'",
                0, node.Name));
            return;
        }

        inStack.Add(node);

        foreach (var dep in node.Dependencies) {
            Visit(dep, visited, inStack, result, diagnostics);
        }

        inStack.Remove(node);
        visited.Add(node);
        result.Add(node);
    }

    private static bool HasErrors(List<WgslDiagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == WgslDiagnosticSeverity.Error);
}

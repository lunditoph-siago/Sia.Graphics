namespace Sia.Graphics.Wgsl;

public sealed class WgslProcessResult
{
    public string CombinedSource { get; init; } = "";
    public List<WgslDiagnostic> Diagnostics { get; init; } = [];
    public bool HasErrors => Diagnostics.Any(d => d.Severity == WgslDiagnosticSeverity.Error);

    public static WgslProcessResult Success(string source) => new() { CombinedSource = source };

    public static WgslProcessResult Failure(List<WgslDiagnostic> diagnostics) => new() { Diagnostics = diagnostics };
}

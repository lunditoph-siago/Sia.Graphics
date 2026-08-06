namespace Sia.Graphics.Wgsl;

public enum WgslDiagnosticSeverity
{
    Error,
    Warning
}

public readonly record struct WgslDiagnostic(
    WgslDiagnosticSeverity Severity,
    string Message,
    int Line,
    string? FilePath)
{
    public override string ToString() => Severity switch {
        WgslDiagnosticSeverity.Error => $"{FilePath}({Line}): error: {Message}",
        WgslDiagnosticSeverity.Warning => $"{FilePath}({Line}): warning: {Message}",
        _ => $"{FilePath}({Line}): {Message}"
    };
}

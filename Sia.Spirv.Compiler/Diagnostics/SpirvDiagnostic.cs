namespace Sia.Spirv.Compiler.Diagnostics;

public sealed record SpirvDiagnostic(
    string Id,
    SpirvDiagnosticSeverity Severity,
    string Message,
    string Method,
    int? IlOffset = null);

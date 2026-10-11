namespace Sia.Spirv.Compiler.Translation;

/// <summary>UTF-16 source range, or byte range for binary input.</summary>
public readonly record struct SourceSpan(int Start, int Length)
{
    public int End => checked(Start + Length);
}

public enum DiagnosticStage { SpirvParse, WgslParse, Validation, SpirvWrite, WgslWrite }

public sealed record ShaderDiagnostic(DiagnosticStage Stage, string Message, SourceSpan Span = default);

public sealed class ShaderException : Exception
{
    public ShaderDiagnostic Diagnostic { get; }

    public ShaderException(DiagnosticStage stage, string message, SourceSpan span = default)
        : base($"{stage}: {message}") => Diagnostic = new(stage, message, span);
}

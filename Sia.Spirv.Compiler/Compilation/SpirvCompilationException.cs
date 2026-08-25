using Sia.Spirv.Compiler.Diagnostics;

namespace Sia.Spirv.Compiler.Compilation;

public sealed class SpirvCompilationException : Exception
{
    public SpirvCompilationException(IReadOnlyList<SpirvDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public SpirvCompilationException(string message)
        : base(message)
    {
        Diagnostics = [];
    }

    public IReadOnlyList<SpirvDiagnostic> Diagnostics { get; }

    private static string CreateMessage(IReadOnlyList<SpirvDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
}

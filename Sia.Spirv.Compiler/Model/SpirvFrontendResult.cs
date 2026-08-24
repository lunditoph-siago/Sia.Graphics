using Sia.Spirv.Compiler.Diagnostics;

namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvFrontendResult(
    IReadOnlyList<SpirvKernel> Kernels,
    IReadOnlyList<SpirvDiagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.All(static diagnostic =>
        diagnostic.Severity != SpirvDiagnosticSeverity.Error);
}

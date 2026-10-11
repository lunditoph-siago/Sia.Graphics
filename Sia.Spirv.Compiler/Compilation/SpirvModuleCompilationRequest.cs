namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvModuleCompilationRequest(ReadOnlyMemory<byte> AssemblyImage, int MetadataToken,
    ReadOnlyMemory<byte> IntrinsicImage = default)
{
    public SpirvCompilationTarget Target { get; init; } = SpirvCompilationTarget.Default;
}

namespace Sia.Spirv.Compiler.Compilation;

/// <summary>An in-memory CIL compilation request, independent of files and native tools.
/// The caller owns the PE buffers and must keep them unchanged for the duration of compilation.</summary>
public sealed record SpirvModuleCompilationRequest(ReadOnlyMemory<byte> AssemblyImage, int MetadataToken,
    ReadOnlyMemory<byte> IntrinsicImage = default)
{
    public SpirvCompilationTarget Target { get; init; } = SpirvCompilationTarget.Default;

    // Compatibility forwarding properties; the request has one target value.
    public SpirvKernelAbi KernelAbi { get => Target.KernelAbi; init => Target = Target with { KernelAbi = value }; }

    public SpirvTargetProfile TargetProfile { get => Target.ResourceLimits; init => Target = Target with { ResourceLimits = value }; }
}

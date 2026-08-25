namespace Sia.Spirv.Runtime;

public sealed record SpirvToolchainInfo(string Llvm, string SpirvTools, string? Naga = null);

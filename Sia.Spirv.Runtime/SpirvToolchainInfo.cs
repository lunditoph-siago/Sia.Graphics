namespace Sia.Spirv.Runtime;

public sealed record SpirvToolchainInfo(string Llvm, string SpirvTools, string? Naga = null, string? NagaSha256 = null,
    string? Translator = null, string? TranslatorSha256 = null);

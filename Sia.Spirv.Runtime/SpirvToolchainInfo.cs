namespace Sia.Spirv.Runtime;

public sealed record SpirvToolchainInfo(string Llvm, string SpirvTools,
    string? Translator = null, string? TranslatorSha256 = null);

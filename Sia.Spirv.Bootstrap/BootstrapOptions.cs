namespace Sia.Spirv.Bootstrap;

internal sealed record BootstrapOptions(
    bool InstallWorkload,
    string DotNetPath,
    IReadOnlyList<string> Sources);

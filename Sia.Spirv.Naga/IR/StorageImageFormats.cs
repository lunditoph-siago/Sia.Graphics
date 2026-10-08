namespace Sia.Spirv.Naga.IR;

/// <summary>WGSL storage formats, indexed by their SPIR-V ImageFormat value.</summary>
internal static class StorageImageFormats
{
    // BGRA has no typed SPIR-V image format. It is emitted as Unknown (zero),
    // which cannot identify BGRA when reading a binary without external metadata.
    private static readonly string[] Names = [
        "bgra8unorm", "rgba32float", "rgba16float", "r32float", "rgba8unorm", "rgba8snorm",
        "rg32float", "rg16float", "rg11b10ufloat", "r16float", "rgba16unorm", "rgb10a2unorm",
        "rg16unorm", "rg8unorm", "r16unorm", "r8unorm", "rgba16snorm", "rg16snorm", "rg8snorm",
        "r16snorm", "r8snorm", "rgba32sint", "rgba16sint", "rgba8sint", "r32sint", "rg32sint",
        "rg16sint", "rg8sint", "r16sint", "r8sint", "rgba32uint", "rgba16uint", "rgba8uint",
        "r32uint", "rgb10a2uint", "rg32uint", "rg16uint", "rg8uint", "r16uint", "r8uint", "r64uint"
    ];
    private static readonly Dictionary<string, uint> Codes = Names.Select((name, index) => (name, index))
        .ToDictionary(pair => pair.name, pair => (uint)pair.index, StringComparer.Ordinal);

    internal static bool TryCode(string name, out uint code) => Codes.TryGetValue(name, out code);
    internal static string? Name(uint code) => code > 0 && code < Names.Length ? Names[code] : null;
    internal static ShaderType.Scalar Component(uint code) => code switch
    {
        >= 21 and <= 29 => ShaderType.I32,
        >= 30 and <= 39 => ShaderType.U32,
        40 => new(ScalarKind.Uint, 8),
        _ => ShaderType.F32
    };
    internal static bool Extended(uint code) => code is not (0 or 1 or 2 or 3 or 4 or 5 or 21 or 22 or 23 or 24 or 30 or 31 or 32 or 33 or 40);
}

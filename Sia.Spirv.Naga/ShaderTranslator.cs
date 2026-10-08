using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;

namespace Sia.Spirv.Naga;

/// <summary>Managed SPIR-V/WGSL translation. Unsupported features produce structured diagnostics.</summary>
public static class ShaderTranslator
{
    public static string SpirvToWgsl(ReadOnlySpan<byte> source, SpirvReadOptions? options = null) =>
        WgslWriter.Write(SpirvReader.Parse(source, options));

    public static byte[] WgslToSpirv(string source, SpirvWriteOptions? options = null) =>
        SpirvWriter.Write(WgslReader.Parse(source), options);
}

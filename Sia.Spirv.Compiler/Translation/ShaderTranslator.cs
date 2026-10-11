using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation;

/// <summary>Managed SPIR-V/WGSL translation. Unsupported features produce structured diagnostics.</summary>
public static class ShaderTranslator
{
    public static string SpirvToWgsl(ReadOnlySpan<byte> source, SpirvCompilationTarget target, SpirvReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target); target.Validate(wgsl: true);
        return WgslWriter.Write(SpirvReader.Parse(source, options), target);
    }

    public static byte[] WgslToSpirv(string source, SpirvCompilationTarget target, SpirvWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target); target.Validate();
        return SpirvWriter.Write(WgslReader.Parse(source), target, options);
    }
}

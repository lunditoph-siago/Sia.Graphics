using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation;

public static class ShaderTranslator
{
    public static string SpirvToWgsl(ReadOnlySpan<byte> source, SpirvCompilationTarget target, SpirvReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate(wgsl: true);
        return SpirvReader.ReadForWgsl(source, options) switch {
            SpirvReader.WgslInput.Canonical input => WgslWriter.WriteCanonical(input.Module, target),
            SpirvReader.WgslInput.Structured input => WgslWriter.Write(input.Module, target),
            _ => throw new InvalidOperationException("Unexpected SPIR-V reader result.")
        };
    }

    public static byte[] WgslToSpirv(string source, SpirvCompilationTarget target, SpirvWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target); target.Validate();
        return SpirvWriter.Write(WgslReader.Parse(source), target, options);
    }
}

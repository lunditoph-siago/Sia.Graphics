using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class ShaderTranslation
{
    // This exercises WGSL lowering directly; it does not pass through SPIR-V.
    internal static string NormalizeWgsl(string source)
        => WgslWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default);
}

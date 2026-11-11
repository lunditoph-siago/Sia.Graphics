using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class WgslWriter
{
    /// <summary>Prepare WGSL using an explicit ABI/stage/resource target contract.</summary>
    public static string Write(Module module, SpirvCompilationTarget target)
        => ShaderOutputPipeline.Write(module, target, WgslBackend.Instance);
}

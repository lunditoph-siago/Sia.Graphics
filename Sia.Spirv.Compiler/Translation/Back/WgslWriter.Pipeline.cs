using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class WgslWriter
{
    public static string Write(Module module, SpirvCompilationTarget target)
        => ShaderOutputPipeline.Write(module, target, WgslBackend.Instance);

    internal static string WriteCanonical(CanonicalModule module, SpirvCompilationTarget target)
    {
        ShaderTargetValidator.ValidateModule(module, target, wgsl: true);
        ModuleValidator.Validate(module);
        return ShaderOutputPipeline.WriteCanonical(module, target, WgslBackend.Instance);
    }
}

using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class WgslWriter
{
    /// <summary>Legalize and emit WGSL without modifying the caller's module.</summary>
    public static string Write(Module module) => Emit(ShaderTargetLowering.ForWgsl(module));

    /// <summary>Prepare WGSL using an explicit ABI/stage/resource target contract.</summary>
    public static string Write(Module module, SpirvCompilationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ShaderTargetValidator.ValidateModule(module, target, wgsl: true);
        var prepared = ShaderTargetLowering.ForWgsl(module);
        ShaderTargetValidator.ValidateModule(prepared, target, wgsl: true);
        string text = Emit(prepared); ShaderTargetValidator.ValidateWgsl(text, target); return text;
    }
}

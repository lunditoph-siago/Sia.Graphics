using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    /// <summary>Legalize and emit SPIR-V bytes without modifying the caller's module.</summary>
    public static byte[] Write(Module module, SpirvCompilationTarget target, SpirvWriteOptions? options = null) => WriteModule(module, target, options).ToBytes();

    /// <summary>Legalize and emit SPIR-V words through the same pipeline as byte output.</summary>
    public static uint[] WriteWords(Module module, SpirvCompilationTarget target, SpirvWriteOptions? options = null) => WriteModule(module, target, options).ToWords();

    private static SpirvBinary WriteModule(Module module, SpirvCompilationTarget target, SpirvWriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ShaderTargetValidator.ValidateModule(module, target, resources: false);
        options ??= new();
        if (options.UseLocalSizeId && target.Environment == "vulkan1.2")
            throw new ShaderException(DiagnosticStage.SpirvWrite, "LocalSizeId requires Vulkan 1.3 or an explicit universal target.");
        var prepared = ShaderTargetLowering.ForSpirv(module, options.PipelineConstants, options.EmitIntegerDivisionChecks,
            options.AdjustCoordinateSpace, options.ClampFragmentDepth, options.ZeroInitializeWorkgroupMemory, options.UseLocalSizeId, target.Version);
        ShaderTargetValidator.ValidateModule(prepared.PhysicalLayout.Canonical, target);
        var binary = Emit(prepared);
        ShaderTargetValidator.ValidateBinary(binary, target);
        return binary;
    }
}

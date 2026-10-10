using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    /// <summary>Legalize and emit SPIR-V bytes without modifying the caller's module.</summary>
    public static byte[] Write(Module module, SpirvWriteOptions? options = null) => WriteModule(module, options).ToBytes();

    /// <summary>Legalize and emit SPIR-V words through the same pipeline as byte output.</summary>
    public static uint[] WriteWords(Module module, SpirvWriteOptions? options = null) => WriteModule(module, options).ToWords();

    private static SpirvBinary WriteModule(Module module, SpirvWriteOptions? options)
    {
        options ??= new();
        if (options.Target is { } target) {
            ShaderTargetValidator.ValidateModule(module, target, resources: false);
            if (options.UseLocalSizeId && target.Environment == "vulkan1.2")
                throw new ShaderException(DiagnosticStage.SpirvWrite, "LocalSizeId requires Vulkan 1.3 or an explicit universal target.");
        }
        var prepared = ShaderTargetLowering.ForSpirv(module, options.PipelineConstants, options.EmitIntegerDivisionChecks,
            options.AdjustCoordinateSpace, options.ClampFragmentDepth, options.ZeroInitializeWorkgroupMemory, options.UseLocalSizeId, options.Target?.Version);
        if (options.Target is { } resourceTarget) ShaderTargetValidator.ValidateModule(prepared.PhysicalLayout.Canonical, resourceTarget);
        var binary = Emit(prepared);
        if (options.Target is { } selected) ShaderTargetValidator.ValidateBinary(binary, selected);
        return binary;
    }
}

using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Back;

internal sealed class SpirvBackend(SpirvWriteOptions options) : IShaderBackend<SpirvEntryPointLowering.Result, SpirvBinary>
{
    public static readonly SpirvBackend Default = new(new());

    public void ValidateInput(Module module, SpirvCompilationTarget target)
    {
        ShaderTargetValidator.ValidateModule(module, target, resources: false);
        if (options.UseLocalSizeId && target.Environment == "vulkan1.2") {
            throw new ShaderException(DiagnosticStage.SpirvWrite,
                "LocalSizeId requires Vulkan 1.3 or an explicit universal target.");
        }
    }

    public SpirvEntryPointLowering.Result Prepare(CanonicalModule module, SpirvCompilationTarget target)
    {
        var canonical = PrepareCanonical(module, options.PipelineConstants, options.EmitIntegerDivisionChecks);
        return SpirvEntryPointLowering.Run(canonical, options.AdjustCoordinateSpace, options.ClampFragmentDepth,
            options.ZeroInitializeWorkgroupMemory, options.UseLocalSizeId, target.Version);
    }

    public void ValidatePrepared(SpirvEntryPointLowering.Result module, SpirvCompilationTarget target)
        => ShaderTargetValidator.ValidateModule(module.PhysicalLayout.Canonical, target);

    public SpirvBinary Emit(SpirvEntryPointLowering.Result module) => SpirvWriter.Emit(module);

    public void ValidateArtifact(SpirvBinary artifact, SpirvCompilationTarget target)
        => ShaderTargetValidator.ValidateBinary(artifact, target);

    internal static CanonicalModule PrepareCanonical(CanonicalModule canonical, IReadOnlyDictionary<string, double>? pipelineConstants, bool integerDivisionChecks = true)
    {
        // Source-language legality has already run at the frontend boundary.
        // Native canonical pointer parameters retain their address spaces here.
        ModuleValidator.Validate(canonical, native: true);
        canonical = ShaderValueLowering.Run(canonical);
        if (pipelineConstants is { } values) {
            canonical = PipelineConstantResolver.Resolve(canonical, values);
        }
        canonical = TargetHelperLowering.Run(canonical);
        canonical = CanonicalReferenceLowering.Run(canonical, lowerPointers: false);
        canonical = InvocationTerminationControlFlow.PrepareSpirv(canonical, DiagnosticStage.SpirvWrite);
        canonical = SpirvRayQueryLowering.Run(canonical);
        return SpirvIntegerArithmeticLowering.Run(canonical, integerDivisionChecks);
    }
}

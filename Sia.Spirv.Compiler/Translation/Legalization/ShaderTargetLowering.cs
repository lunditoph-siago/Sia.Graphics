using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Target pass ordering over structured IR. Passes borrow their input without modifying it.</summary>
internal static class ShaderTargetLowering
{
    public static Module ForWgsl(Module module)
    {
        ShaderTargetValidator.ValidateWgslInvocationFeatures(module);
        ModuleValidator.Validate(module);
        module = WgslEntryPointLowering.Run(module);
        ModuleValidator.Validate(module);
        module = StructuredControlFlowLowering.Run(CanonicalShaderPipeline.Prepare(module));
        module = HelperInliner.RunPointers(module);
        module = CollectiveReadRecovery.Run(module);
        UniformityAnalysis.Validate(module, DiagnosticStage.WgslWrite);
        module = InvocationTerminationControlFlow.Run(module, DiagnosticStage.WgslWrite);
        module = WgslTerminationLowering.Run(module);
        ModuleValidator.Validate(module);
        UniformityAnalysis.Validate(module, DiagnosticStage.WgslWrite);
        PointerAliasAnalysis.Validate(module, DiagnosticStage.WgslWrite);
        module = QueryStateLowering.Run(module);
        ModuleValidator.Validate(module);
        module = WgslMemoryLowering.Run(module);
        ModuleValidator.Validate(module);
        if (module.Constants.Any(c => (c.IsOverride || c.IsSpecialization)
            && c.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 8 }))
            throw new ShaderException(DiagnosticStage.WgslWrite, "64-bit integer specialization constants require PipelineConstantResolver resolution before WGSL writing.");
        module = WgslLayoutLowering.Run(module);
        ModuleValidator.Validate(module);
        UniformityAnalysis.Validate(module, DiagnosticStage.WgslWrite);
        PointerAliasAnalysis.Validate(module, DiagnosticStage.WgslWrite);
        module = WgslBuiltinNameLowering.Run(module);
        return module;
    }

    public static Module ForSpirv(Module module, IReadOnlyDictionary<string, double>? pipelineConstants, bool integerDivisionChecks = true)
    {
        module = StructuredControlFlowLowering.Run(CanonicalShaderPipeline.Prepare(module));
        if (pipelineConstants is { } values) {
            module = PipelineConstantResolver.Resolve(module, values);
            ModuleValidator.Validate(module);
        }
        module = HelperInliner.RunQueries(module);
        ModuleValidator.Validate(module);
        module = HelperInliner.RunNonFunctionPointers(module);
        ModuleValidator.Validate(module);
        module = HelperInliner.RunPointers(module);
        ModuleValidator.Validate(module);
        module = InvocationTerminationControlFlow.Run(module, DiagnosticStage.SpirvWrite);
        module = SpirvIntegerArithmeticLowering.Run(module, integerDivisionChecks);
        return module;
    }

    public static SpirvEntryPointLowering.Result ForSpirv(Module module, IReadOnlyDictionary<string, double>? pipelineConstants,
        bool integerDivisionChecks, bool adjustCoordinateSpace, bool clampFragmentDepth, bool zeroInitializeWorkgroupMemory = true, bool useLocalSizeId = false, uint? version = null)
        => SpirvEntryPointLowering.Run(ForSpirv(module, pipelineConstants, integerDivisionChecks), adjustCoordinateSpace, clampFragmentDepth, zeroInitializeWorkgroupMemory, useLocalSizeId, version);
}

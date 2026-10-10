using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Target pass ordering with explicit adapters for unmigrated families. Passes borrow their input.</summary>
internal static class ShaderTargetLowering
{
    public static Module ForWgsl(Module module)
        => ForWgsl(CanonicalShaderPipeline.Prepare(module));

    internal static Module ForWgsl(CanonicalModule canonical)
    {
        ModuleValidator.Validate(canonical, native: true);
        ShaderTargetValidator.ValidateWgslInvocationFeatures(canonical);
        canonical = WgslEntryPointLowering.Run(canonical);
        canonical = CanonicalHelperInliner.RunPointers(canonical);
        canonical = CollectiveReadRecovery.Run(canonical);
        UniformityAnalysis.Validate(canonical.Declarations, canonical.Functions, DiagnosticStage.WgslWrite);
        // Check original non-returning control before WGSL introduces a return.
        canonical = WgslTerminationLowering.Run(canonical);
        // Remaining WGSL memory/query/layout passes consume structured target data.
        // All owned graph semantics stay authoritative until this explicit boundary.
        var module = StructuredControlFlowLowering.Run(canonical);
        UniformityAnalysis.Validate(module, DiagnosticStage.WgslWrite);
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
        => StructuredControlFlowLowering.Run(PrepareSpirv(CanonicalShaderPipeline.Prepare(module), pipelineConstants, integerDivisionChecks));

    internal static CanonicalModule PrepareSpirv(CanonicalModule canonical, IReadOnlyDictionary<string, double>? pipelineConstants, bool integerDivisionChecks = true)
    {
        // Source-language legality has already run at the frontend boundary.
        // Native canonical pointer parameters retain their address spaces here.
        ModuleValidator.Validate(canonical, native: true);
        if (pipelineConstants is { } values) canonical = PipelineConstantResolver.Resolve(canonical, values);
        canonical = CanonicalHelperInliner.RunPointers(canonical);
        canonical = InvocationTerminationControlFlow.PrepareSpirv(canonical, DiagnosticStage.SpirvWrite);
        canonical = SpirvRayQueryLowering.Run(canonical);
        return SpirvIntegerArithmeticLowering.Run(canonical, integerDivisionChecks);
    }

    public static SpirvEntryPointLowering.Result ForSpirv(Module module, IReadOnlyDictionary<string, double>? pipelineConstants,
        bool integerDivisionChecks, bool adjustCoordinateSpace, bool clampFragmentDepth, bool zeroInitializeWorkgroupMemory = true, bool useLocalSizeId = false, uint? version = null)
        => SpirvEntryPointLowering.Run(PrepareSpirv(CanonicalShaderPipeline.Prepare(module), pipelineConstants, integerDivisionChecks), adjustCoordinateSpace, clampFragmentDepth, zeroInitializeWorkgroupMemory, useLocalSizeId, version);
}

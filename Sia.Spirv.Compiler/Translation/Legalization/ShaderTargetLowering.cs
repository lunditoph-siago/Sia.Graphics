using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Target pass ordering with explicit adapters for unmigrated families. Passes borrow their input.</summary>
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
        => StructuredControlFlowLowering.Run(PrepareSpirv(CanonicalShaderPipeline.Prepare(module), pipelineConstants, integerDivisionChecks));

    internal static CanonicalModule PrepareSpirv(CanonicalModule canonical, IReadOnlyDictionary<string, double>? pipelineConstants, bool integerDivisionChecks = true)
    {
        // Source-language legality has already run at the frontend boundary.
        // Native canonical pointer parameters retain their address spaces here.
        ModuleValidator.Validate(canonical, native: true);
        if (pipelineConstants is { } values) canonical = PipelineConstantResolver.Resolve(canonical, values);
        canonical = CanonicalHelperInliner.RunPointers(canonical);
        // These semantic families still require the legacy module adapter.
        // Ordinary graphs reach integer/entry/layout preparation without reconstruction.
        bool legacy = canonical.Declarations.Functions.Any(f => f.ReturnType is ShaderType.Pointer
                || f.Arguments.Any(a => a.Type is ShaderType.Pointer)
                && (f.Stage is null || f.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.RayQuery })))
            || canonical.Functions.Values.Any(g => g.Blocks.Any(b => b.Terminator is ControlFlowTerminator.InvocationKill))
            || canonical.Declarations.Functions.Any(f => canonical.DeferredFunctions.ContainsKey(f.Name)
                && InvocationTerminationControlFlow.NeedsRelocation(f.Body));
        if (legacy) {
            var module = StructuredControlFlowLowering.Run(canonical);
            module = HelperInliner.RunQueries(module);
            ModuleValidator.Validate(module);
            module = HelperInliner.RunNonFunctionPointers(module);
            ModuleValidator.Validate(module);
            module = HelperInliner.RunPointers(module);
            ModuleValidator.Validate(module);
            module = InvocationTerminationControlFlow.Run(module, DiagnosticStage.SpirvWrite);
            canonical = SpirvControlFlowLowering.Capture(module);
        }
        return SpirvIntegerArithmeticLowering.Run(canonical, integerDivisionChecks);
    }

    public static SpirvEntryPointLowering.Result ForSpirv(Module module, IReadOnlyDictionary<string, double>? pipelineConstants,
        bool integerDivisionChecks, bool adjustCoordinateSpace, bool clampFragmentDepth, bool zeroInitializeWorkgroupMemory = true, bool useLocalSizeId = false, uint? version = null)
        => SpirvEntryPointLowering.Run(PrepareSpirv(CanonicalShaderPipeline.Prepare(module), pipelineConstants, integerDivisionChecks), adjustCoordinateSpace, clampFragmentDepth, zeroInitializeWorkgroupMemory, useLocalSizeId, version);
}

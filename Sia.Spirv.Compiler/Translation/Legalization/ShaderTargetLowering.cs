using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Target pass ordering with explicit adapters for unmigrated families. Passes borrow their input.</summary>
internal static class ShaderTargetLowering
{
    internal static Module ForStructured(CanonicalModule canonical, bool nativeValidation = false)
    {
        canonical = LowerValues(canonical);
        if (RequiresTargetHelperInlining(canonical)) {
            canonical = CanonicalHelperInliner.RunTargetHelpers(canonical);
        }
        if (canonical.Functions.Values.Any(static graph => graph.Blocks
            .Any(static block => block.Terminator is ControlFlowTerminator.TaskDispatch))) {
            canonical = InvocationTerminationControlFlow.PrepareSpirv(canonical, DiagnosticStage.Validation);
        }
        canonical = CanonicalReferenceLowering.Run(canonical);
        return StructuredControlFlowLowering.Run(canonical, nativeValidation);
    }

    private static bool RequiresTargetHelperInlining(CanonicalModule canonical)
        => canonical.Functions.Values.Any(static graph => graph.Blocks.Any(static block =>
                block.Parameters.Any(static parameter => parameter.Type is ShaderType.Pointer || CanonicalTypes.Resource(parameter.Type))))
            || canonical.Functions.Values.Any(PointerAliasAnalysis.HasPointerOperations)
            || canonical.Functions.Values.SelectMany(static graph => graph.Blocks)
                .SelectMany(static block => block.Instructions)
                .Any(static instruction => instruction.Result?.Type is ShaderType.Pointer { Base: ShaderType.Pointer })
            || canonical.Declarations.Functions.Any(function =>
                function.ReturnType is ShaderType.Pointer && canonical.EntryFunctions.Contains(function.Name));

    internal static ShaderFunction ForStructured(ControlFlowFunction graph, Module module)
        => StructuredControlFlowLowering.Run(CanonicalReferenceLowering.Run(UndefinedValueLowering.Run(graph)), module);

    public static Module ForWgsl(Module module)
        => ForWgsl(CanonicalShaderPipeline.Prepare(module));

    internal static Module ForWgsl(CanonicalModule canonical)
    {
        ModuleValidator.Validate(canonical, native: true);
        ShaderTargetValidator.ValidateWgslInvocationFeatures(canonical);
        canonical = LowerValues(canonical);
        canonical = WgslEntryPointLowering.Run(canonical);
        canonical = CanonicalHelperInliner.RunTargetHelpers(canonical, taskDispatch: true);
        canonical = CanonicalReferenceLowering.Run(canonical);
        canonical = CollectiveReadRecovery.Run(canonical);
        canonical = WgslUniformInputLowering.Run(canonical);
        UniformityAnalysis.Validate(canonical.Declarations, canonical.Functions, DiagnosticStage.WgslWrite);
        // Check original non-returning control before WGSL introduces a return.
        canonical = WgslTerminationLowering.Run(canonical);
        canonical = WgslImageAtomicLowering.Run(canonical);
        // Remaining WGSL memory/query/layout passes consume structured target data.
        // All owned graph semantics stay authoritative until this explicit boundary.
        canonical = WgslValueLowering.Run(canonical);
        var module = StructuredControlFlowLowering.Run(canonical, compactValues: true);
        if (canonical.Functions.Values.SelectMany(static graph => graph.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction.Operation is ValueOperation.Builtin builtin
                && SubgroupBuiltins.Contains(builtin.Function))) {
            module.Enables.Add("subgroups");
        }
        return LowerWgslModule(module);
    }

    private static Module LowerWgslModule(Module module)
    {
        UniformityAnalysis.Validate(module, DiagnosticStage.WgslWrite);
        ModuleValidator.Validate(module);
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
        module = WgslLetPromotion.Run(module);
        ModuleValidator.Validate(module);
        return module;
    }

    public static Module ForSpirv(Module module, IReadOnlyDictionary<string, double>? pipelineConstants, bool integerDivisionChecks = true)
        => ForStructured(PrepareSpirv(CanonicalShaderPipeline.Prepare(module), pipelineConstants, integerDivisionChecks));

    internal static CanonicalModule PrepareSpirv(CanonicalModule canonical, IReadOnlyDictionary<string, double>? pipelineConstants, bool integerDivisionChecks = true)
    {
        // Source-language legality has already run at the frontend boundary.
        // Native canonical pointer parameters retain their address spaces here.
        ModuleValidator.Validate(canonical, native: true);
        canonical = LowerValues(canonical);
        if (pipelineConstants is { } values) {
            canonical = PipelineConstantResolver.Resolve(canonical, values);
        }
        canonical = CanonicalHelperInliner.RunTargetHelpers(canonical);
        canonical = CanonicalReferenceLowering.Run(canonical, lowerPointers: false);
        canonical = InvocationTerminationControlFlow.PrepareSpirv(canonical, DiagnosticStage.SpirvWrite);
        canonical = SpirvRayQueryLowering.Run(canonical);
        return SpirvIntegerArithmeticLowering.Run(canonical, integerDivisionChecks);
    }

    private static CanonicalModule LowerValues(CanonicalModule canonical)
    {
        canonical = UndefinedValueLowering.Run(canonical);
        canonical = AtomicStorageLowering.Run(canonical);
        return MatrixStorageLowering.Run(canonical);
    }

    public static SpirvEntryPointLowering.Result ForSpirv(Module module, IReadOnlyDictionary<string, double>? pipelineConstants,
        bool integerDivisionChecks, bool adjustCoordinateSpace, bool clampFragmentDepth, bool zeroInitializeWorkgroupMemory = true, bool useLocalSizeId = false, uint? version = null)
        => SpirvEntryPointLowering.Run(PrepareSpirv(CanonicalShaderPipeline.Prepare(module), pipelineConstants, integerDivisionChecks), adjustCoordinateSpace, clampFragmentDepth, zeroInitializeWorkgroupMemory, useLocalSizeId, version);
}

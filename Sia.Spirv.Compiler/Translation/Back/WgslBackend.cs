using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Back;

internal sealed class WgslBackend : IShaderBackend<Module, string>
{
    public static readonly WgslBackend Instance = new();

    private WgslBackend() { }

    public void ValidateInput(Module module, SpirvCompilationTarget target)
        => ShaderTargetValidator.ValidateModule(module, target, wgsl: true);

    public void ValidatePrepared(Module module, SpirvCompilationTarget target)
        => ValidateInput(module, target);

    public string Emit(Module module) => WgslWriter.Emit(module);

    public void ValidateArtifact(string artifact, SpirvCompilationTarget target)
        => ShaderTargetValidator.ValidateWgsl(artifact, target);

    public Module Prepare(CanonicalModule canonical, SpirvCompilationTarget target)
    {
        ModuleValidator.Validate(canonical, native: true);
        ShaderTargetValidator.ValidateWgslInvocationFeatures(canonical);
        canonical = ShaderValueLowering.Run(canonical);
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
}

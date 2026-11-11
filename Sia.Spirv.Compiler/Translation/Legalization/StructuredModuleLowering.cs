using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Prepares canonical graphs for consumers that need a structured shader module.</summary>
internal static class StructuredModuleLowering
{
    internal static Module Run(CanonicalModule canonical, bool nativeValidation = false)
    {
        canonical = ShaderValueLowering.Run(canonical);
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

    internal static ShaderFunction Run(ControlFlowFunction graph, Module module)
        => StructuredControlFlowLowering.Run(CanonicalReferenceLowering.Run(UndefinedValueLowering.Run(graph)), module);
}

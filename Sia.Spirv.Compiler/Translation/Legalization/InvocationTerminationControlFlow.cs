using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Relocate native termination exposed by helper expansion out of continue constructs.
/// Both targets use verified SSA reconstruction; the native terminator itself is retained.</summary>
internal static class InvocationTerminationControlFlow
{
    public static Module Run(Module input, DiagnosticStage stage)
    {
        if (!input.Functions.Any(f => NeedsRelocation(f.Body))) return input;
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions) {
            if (!NeedsRelocation(function.Body)) { output.Functions.Add(function); continue; }
            if (!StructuredControlFlowReader.TryRead(function, input, out var graph, out var reason))
                throw new ShaderException(stage, "Invocation termination relocation requires canonical control flow: " + reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, input);
            output.Functions.Add(StructuredControlFlowLowering.Run(graph!, input, relocateTerminatingContinuing: true));
        }
        ModuleValidator.Validate(output);
        return output;
    }

    private static bool ContainsKill(Block body) => body.Statements.Any(s => s switch {
        Statement.InvocationKill => true, Statement.Nested n => ContainsKill(n.Body),
        Statement.If i => ContainsKill(i.Accept) || ContainsKill(i.Reject),
        Statement.Loop l => ContainsKill(l.Body) || ContainsKill(l.Continuing),
        Statement.Switch sw => sw.Cases.Any(c => ContainsKill(c.Body)), _ => false
    });
    internal static bool NeedsRelocation(Block body) => body.Statements.Any(s => s switch {
        Statement.Loop l => ContainsKill(l.Continuing) || NeedsRelocation(l.Body) || NeedsRelocation(l.Continuing),
        Statement.Nested n => NeedsRelocation(n.Body),
        Statement.If i => NeedsRelocation(i.Accept) || NeedsRelocation(i.Reject),
        Statement.Switch sw => sw.Cases.Any(c => NeedsRelocation(c.Body)), _ => false
    });
}

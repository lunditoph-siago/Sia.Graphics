namespace Sia.Spirv.Compiler.Translation.IR.ControlFlow;

/// <summary>Compilation-owned function graphs over borrowed declarations.
/// Deferred bodies cross the temporary structured adapter explicitly.</summary>
internal sealed record CanonicalModule(Module Declarations,
    IReadOnlyDictionary<string, ControlFlowFunction> Functions,
    IReadOnlyDictionary<string, string> DeferredFunctions,
    IReadOnlySet<string> EntryFunctions);

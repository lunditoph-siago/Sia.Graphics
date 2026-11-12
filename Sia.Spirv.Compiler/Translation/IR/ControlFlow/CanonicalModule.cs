namespace Sia.Spirv.Compiler.Translation.IR.ControlFlow;

internal sealed record CanonicalModule(Module Declarations,
    IReadOnlyDictionary<string, ControlFlowFunction> Functions,
    IReadOnlyDictionary<string, string> DeferredFunctions,
    IReadOnlySet<string> EntryFunctions);

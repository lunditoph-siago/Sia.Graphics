namespace Sia.Spirv.Compiler.Translation.IR;

public sealed record SpirvAtomicMemory(uint Scope, uint Semantics, uint? UnequalSemantics = null);

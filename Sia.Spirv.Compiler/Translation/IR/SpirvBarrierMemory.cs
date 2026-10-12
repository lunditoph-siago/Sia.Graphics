namespace Sia.Spirv.Compiler.Translation.IR;

/// <summary>Native barrier operands. A null execution scope denotes a memory-only fence.</summary>
public sealed record SpirvBarrierMemory(uint Scope, uint Semantics, uint? ExecutionScope = null);

namespace Sia.Spirv.Compiler.Translation.IR;

public sealed record SpirvBarrierMemory(uint Scope, uint Semantics, uint? ExecutionScope = null);

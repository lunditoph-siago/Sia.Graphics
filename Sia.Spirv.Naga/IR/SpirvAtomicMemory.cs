namespace Sia.Spirv.Naga.IR;

/// <summary>SPIR-V scope and memory-semantics operands retained through translation.</summary>
/// <remarks>Shader modules require ordinary 32-bit constants for these operands.</remarks>
public sealed record SpirvAtomicMemory(uint Scope, uint Semantics, uint? UnequalSemantics = null);

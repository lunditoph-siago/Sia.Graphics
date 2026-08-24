namespace Sia.Spirv.Compiler.IL;

public sealed record CilBasicBlock(
    int Id,
    int StartOffset,
    IReadOnlyList<CilInstruction> Instructions,
    IReadOnlyList<int> Successors);

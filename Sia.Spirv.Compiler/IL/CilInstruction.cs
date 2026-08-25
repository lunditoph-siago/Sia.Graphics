using System.Reflection.Emit;

namespace Sia.Spirv.Compiler.IL;

public sealed record CilInstruction(
    int Offset,
    OpCode OpCode,
    object? Operand,
    int Size)
{
    public int EndOffset => Offset + Size;
}

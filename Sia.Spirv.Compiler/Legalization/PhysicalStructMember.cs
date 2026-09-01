namespace Sia.Spirv.Compiler.Legalization;

public sealed record PhysicalStructMember(
    int PhysicalIndex,
    int? LogicalFieldIndex,
    int Offset,
    int Alignment,
    int Size)
{
    public bool IsPadding => LogicalFieldIndex == null;
}

using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

public sealed record PhysicalStructLayout(
    ShaderStructType LogicalType,
    ShaderAddressSpace AddressSpace,
    int Alignment,
    int Size,
    int ArrayStride,
    IReadOnlyList<PhysicalStructMember> Members)
{
    public PhysicalStructMember GetLogicalMember(int logicalFieldIndex)
    {
        if (logicalFieldIndex < 0 || logicalFieldIndex >= LogicalType.Fields.Count) {
            throw new ArgumentOutOfRangeException(nameof(logicalFieldIndex));
        }
        return Members.Single(member => member.LogicalFieldIndex == logicalFieldIndex);
    }
}

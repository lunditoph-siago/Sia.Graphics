using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

public sealed class ShaderLayoutEngine : IShaderLayoutEngine
{
    public PhysicalStructLayout Legalize(
        ShaderStructType type,
        ShaderAddressSpace addressSpace)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Fields.Count == 0) {
            throw new InvalidDataException(
                $"Shader struct '{type.Name}' has no instance fields.");
        }

        var members = new List<PhysicalStructMember>();
        var offset = 0;
        var structAlignment = addressSpace == ShaderAddressSpace.Uniform ? 16 : 1;
        for (var logicalFieldIndex = 0;
            logicalFieldIndex < type.Fields.Count;
            logicalFieldIndex++) {
            var field = type.Fields[logicalFieldIndex];
            var alignment = SpirvTypeLayout.GetAlignment(field.Type);
            var size = SpirvTypeLayout.GetSize(field.Type);
            var fieldOffset = AlignUp(offset, alignment);
            AddPadding(members, offset, fieldOffset - offset);
            members.Add(new PhysicalStructMember(
                members.Count,
                logicalFieldIndex,
                fieldOffset,
                alignment,
                size));
            offset = checked(fieldOffset + size);
            structAlignment = Math.Max(structAlignment, alignment);
        }

        var sizeAligned = AlignUp(offset, structAlignment);
        AddPadding(members, offset, sizeAligned - offset);
        return new PhysicalStructLayout(
            type,
            addressSpace,
            structAlignment,
            sizeAligned,
            sizeAligned,
            members);
    }

    private static void AddPadding(
        List<PhysicalStructMember> members,
        int offset,
        int size)
    {
        if (size == 0) {
            return;
        }
        if (size < 0 || size % 4 != 0) {
            throw new InvalidDataException(
                $"Shader struct padding must be a non-negative multiple of four bytes, not {size}.");
        }
        members.Add(new PhysicalStructMember(
            members.Count,
            null,
            offset,
            4,
            size));
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

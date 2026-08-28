namespace Sia.Spirv.Runtime;

public sealed record SpirvResourceBinding(
    string Name,
    string Kind,
    string Access,
    string ElementType,
    int DescriptorSet,
    int Binding,
    int Alignment = 0,
    int Size = 0,
    int ArrayStride = 0,
    IReadOnlyList<SpirvStructFieldLayout>? Fields = null);

public sealed record SpirvStructFieldLayout(
    string Name,
    string Type,
    int Offset,
    int Alignment,
    int Size);

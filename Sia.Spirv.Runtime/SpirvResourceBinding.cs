namespace Sia.Spirv.Runtime;

public sealed record SpirvResourceBinding(
    string Name,
    string Kind,
    string Access,
    string ElementType,
    int DescriptorSet,
    int Binding);

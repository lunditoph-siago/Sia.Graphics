namespace Sia.Spirv.Runtime;

public sealed record SpirvBufferRequirements(
    int StorageBuffers,
    int UniformBuffers,
    ulong StorageBufferBindingSize,
    ulong UniformBufferBindingSize);

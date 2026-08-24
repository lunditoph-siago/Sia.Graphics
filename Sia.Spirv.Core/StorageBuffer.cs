namespace Sia.Spirv;

public readonly ref struct StorageBuffer<T>
    where T : unmanaged
{
    public ref T this[uint index] =>
        throw new PlatformNotSupportedException(
            "Storage buffers can only be accessed from a compiled SPIR-V kernel.");
}

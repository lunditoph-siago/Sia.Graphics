namespace Sia.Spirv;

public readonly ref struct ReadOnlyStorageBuffer<T>
    where T : unmanaged
{
    public ref readonly T this[uint index] =>
        throw new PlatformNotSupportedException(
            "Read-only storage buffers can only be accessed from a compiled SPIR-V kernel.");
}

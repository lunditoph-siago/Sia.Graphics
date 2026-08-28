namespace Sia.Spirv;

public readonly ref struct WorkgroupMemory<T>
    where T : unmanaged
{
    public ref T this[uint index] {
        [SpirvIntrinsic(IntrinsicKind.BufferIndex)]
        get => throw new PlatformNotSupportedException(
            "Workgroup memory can only be accessed from a compiled SPIR-V kernel.");
    }

    [SpirvIntrinsic(IntrinsicKind.AtomicAdd)]
    public T AtomicAdd(uint index, T value) =>
        throw new PlatformNotSupportedException(
            "Workgroup atomics can only be used from a compiled SPIR-V kernel.");

    [SpirvIntrinsic(IntrinsicKind.AtomicExchange)]
    public T AtomicExchange(uint index, T value) =>
        throw new PlatformNotSupportedException(
            "Workgroup atomics can only be used from a compiled SPIR-V kernel.");
}

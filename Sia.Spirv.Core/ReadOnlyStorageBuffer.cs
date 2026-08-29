namespace Sia.Spirv;

public readonly struct ReadOnlyStorageBuffer<T>
    where T : unmanaged
{
    private readonly ReadOnlyMemory<T> _memory;

    public ReadOnlyStorageBuffer(ReadOnlyMemory<T> memory)
    {
        _memory = memory;
    }

    public ReadOnlyStorageBuffer(T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        _memory = array;
    }

    public ref readonly T this[uint index] {
        [SpirvIntrinsic(IntrinsicKind.BufferIndex)]
        get => ref _memory.Span[unchecked((int)index)];
    }
}

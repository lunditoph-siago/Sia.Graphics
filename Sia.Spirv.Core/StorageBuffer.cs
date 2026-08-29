using System.Runtime.CompilerServices;

namespace Sia.Spirv;

public readonly struct StorageBuffer<T>
    where T : unmanaged
{
    private readonly Memory<T> _memory;

    public StorageBuffer(Memory<T> memory)
    {
        _memory = memory;
    }

    public StorageBuffer(T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        _memory = array;
    }

    public ref T this[uint index] {
        [SpirvIntrinsic(IntrinsicKind.BufferIndex)]
        get => ref _memory.Span[unchecked((int)index)];
    }

    [SpirvIntrinsic(IntrinsicKind.AtomicAdd)]
    public T AtomicAdd(uint index, T value)
    {
        EnsureAtomicElementType();
        ref var location = ref this[index];
        var addend = Unsafe.As<T, int>(ref value);
        var updated = Interlocked.Add(ref Unsafe.As<T, int>(ref location), addend);
        var previous = unchecked(updated - addend);
        return Unsafe.As<int, T>(ref previous);
    }

    [SpirvIntrinsic(IntrinsicKind.AtomicExchange)]
    public T AtomicExchange(uint index, T value)
    {
        EnsureAtomicElementType();
        ref var location = ref this[index];
        var replacement = Unsafe.As<T, int>(ref value);
        var previous = Interlocked.Exchange(
            ref Unsafe.As<T, int>(ref location),
            replacement);
        return Unsafe.As<int, T>(ref previous);
    }

    private static void EnsureAtomicElementType()
    {
        if (typeof(T) != typeof(int) && typeof(T) != typeof(uint)) {
            throw new NotSupportedException(
                $"Storage-buffer atomics require int or uint elements, not {typeof(T)}.");
        }
    }
}

namespace Sia.WebGPU;

public readonly unsafe record struct WgpuHandle<T>(nint Handle)
    where T : unmanaged
{
    public bool IsNull => Handle == 0;

    public nint DangerousGetHandle() => Handle;

    internal T* Pointer => (T*)Handle;

    internal static WgpuHandle<T> FromPointer(T* pointer)
    {
        if (pointer == null) {
            throw new WgpuException($"WebGPU returned a null {typeof(T).Name} handle.");
        }

        return new WgpuHandle<T>((nint)pointer);
    }

    internal static WgpuHandle<T> FromOptionalPointer(T* pointer) =>
        new((nint)pointer);

    public override string ToString() => IsNull
        ? $"{typeof(T).Name}<null>"
        : $"{typeof(T).Name}#0x{Handle:X}";
}

namespace Sia.WebGPU;

public readonly record struct WgpuResource<T>(WgpuHandle<T> Handle)
    where T : unmanaged;

public delegate void WgpuReleaseHandler<T>(ref WgpuHandle<T> handle)
    where T : unmanaged;

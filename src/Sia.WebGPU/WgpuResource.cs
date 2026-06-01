namespace Sia.WebGPU;

/// <summary>A WebGPU capability component owned by a Sia world.</summary>
public readonly record struct WgpuResource<T>(WgpuHandle<T> Handle)
    where T : unmanaged;

/// <summary>Releases a canonical WebGPU handle and clears it.</summary>
public delegate void WgpuReleaseHandler<T>(ref WgpuHandle<T> handle)
    where T : unmanaged;

namespace Sia.WebGPU;

public readonly record struct WgpuBufferInfo(
    ulong Size,
    WGPUBufferUsage Usage);

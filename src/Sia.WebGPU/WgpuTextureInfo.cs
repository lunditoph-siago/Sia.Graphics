namespace Sia.WebGPU;

public readonly record struct WgpuTextureInfo(
    WGPUExtent3D Size,
    WGPUTextureFormat Format,
    WGPUTextureUsage Usage,
    uint MipLevelCount,
    uint SampleCount);

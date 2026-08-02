namespace Sia.WebGPU;

public readonly record struct WgpuTextureInfo(
    WGPUExtent3D Size,
    WGPUTextureDimension Dimension,
    WGPUTextureFormat Format,
    WGPUTextureUsage Usage,
    uint MipLevelCount,
    uint SampleCount);

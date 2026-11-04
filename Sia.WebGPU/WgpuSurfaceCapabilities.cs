namespace Sia.WebGPU;

public readonly record struct WgpuSurfaceCapabilities(
    WGPUTextureUsage Usages,
    WGPUTextureFormat[] Formats,
    WGPUPresentMode[] PresentModes,
    WGPUCompositeAlphaMode[] AlphaModes);

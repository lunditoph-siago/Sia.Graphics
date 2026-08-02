using Sia.RenderGraph;

namespace Sia.WebGPU;

public readonly record struct WgpuRenderGraphTexturePlan(
    CompiledRenderGraphTexture Resource,
    WGPUTextureDimension Dimension,
    WGPUTextureFormat Format,
    WGPUTextureUsage Usage);

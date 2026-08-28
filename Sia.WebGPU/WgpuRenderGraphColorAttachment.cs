using Sia.RenderGraph;

namespace Sia.WebGPU;

public readonly record struct WgpuRenderGraphColorAttachment(
    RenderGraphTextureHandle Texture,
    WGPULoadOp LoadOp,
    WGPUStoreOp StoreOp = WGPUStoreOp.Store,
    WGPUColor ClearValue = default,
    RenderGraphTextureSubresourceRange Subresources = default,
    bool Cacheable = true);

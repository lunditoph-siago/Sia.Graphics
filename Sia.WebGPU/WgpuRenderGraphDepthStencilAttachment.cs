using Sia.RenderGraph;

namespace Sia.WebGPU;

public readonly record struct WgpuRenderGraphDepthStencilAttachment(
    RenderGraphTextureHandle Texture,
    WGPULoadOp DepthLoadOp,
    WGPUStoreOp DepthStoreOp = WGPUStoreOp.Store,
    float DepthClearValue = 1.0f,
    bool DepthReadOnly = false,
    WGPULoadOp StencilLoadOp = WGPULoadOp.Undefined,
    WGPUStoreOp StencilStoreOp = WGPUStoreOp.Undefined,
    uint StencilClearValue = 0,
    bool StencilReadOnly = true,
    RenderGraphTextureSubresourceRange Subresources = default,
    bool Cacheable = true);

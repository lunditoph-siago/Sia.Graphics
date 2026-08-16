using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public readonly record struct WgpuReactiveRenderGraphDepthStencilAttachment(
    RenderGraphTextureKey Texture,
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

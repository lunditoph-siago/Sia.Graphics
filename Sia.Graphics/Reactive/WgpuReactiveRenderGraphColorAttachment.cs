using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public readonly record struct WgpuReactiveRenderGraphColorAttachment(
    RenderGraphTextureKey Texture,
    WGPULoadOp LoadOp,
    WGPUStoreOp StoreOp = WGPUStoreOp.Store,
    WGPUColor ClearValue = default,
    RenderGraphTextureSubresourceRange Subresources = default,
    bool Cacheable = true);

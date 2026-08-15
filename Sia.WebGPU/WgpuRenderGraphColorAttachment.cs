using Sia.RenderGraph;

namespace Sia.WebGPU;

/// <summary>
/// One color attachment a pass wants to draw into via
/// <see cref="WgpuRenderGraphPassContext.GetOrBeginRenderPass"/>. <see cref="LoadOp"/>/
/// <see cref="ClearValue"/> only take effect for whichever pass in a merged group is the first
/// to actually open the physical render pass — later passes joining an already-open pass draw
/// on top of what's already there regardless of what they request, so a pass that might run
/// after a neighbor it could merge with should request <see cref="WGPULoadOp.Load"/> to stay
/// correct whether or not the merge actually happens that frame.
/// </summary>
public readonly record struct WgpuRenderGraphColorAttachment(
    RenderGraphTextureHandle Texture,
    WGPULoadOp LoadOp,
    WGPUStoreOp StoreOp = WGPUStoreOp.Store,
    WGPUColor ClearValue = default,
    RenderGraphTextureSubresourceRange Subresources = default,
    bool Cacheable = true);

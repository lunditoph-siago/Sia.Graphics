namespace Sia.RenderGraph;

public readonly record struct RenderGraphTextureAccess(
    RenderGraphTextureHandle Texture,
    RenderGraphAccess Access,
    RenderGraphTextureUsage Usage,
    RenderGraphTextureSubresourceRange Subresources);

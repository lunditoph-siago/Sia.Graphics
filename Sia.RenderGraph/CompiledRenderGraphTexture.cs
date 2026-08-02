namespace Sia.RenderGraph;

public readonly record struct CompiledRenderGraphTexture(
    RenderGraphTextureHandle Handle,
    RenderGraphTextureDescriptor Descriptor,
    RenderGraphTextureUsage Usage,
    RenderGraphResourceLifetime Lifetime,
    bool IsImported,
    bool IsExported);

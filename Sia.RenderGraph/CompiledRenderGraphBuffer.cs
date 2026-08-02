namespace Sia.RenderGraph;

public readonly record struct CompiledRenderGraphBuffer(
    RenderGraphBufferHandle Handle,
    RenderGraphBufferDescriptor Descriptor,
    RenderGraphBufferUsage Usage,
    RenderGraphResourceLifetime Lifetime,
    bool IsImported,
    bool IsExported);

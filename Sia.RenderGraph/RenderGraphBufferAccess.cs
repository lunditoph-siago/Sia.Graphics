namespace Sia.RenderGraph;

public readonly record struct RenderGraphBufferAccess(
    RenderGraphBufferHandle Buffer,
    RenderGraphAccess Access,
    RenderGraphBufferUsage Usage,
    RenderGraphBufferRange Range);

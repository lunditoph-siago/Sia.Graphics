namespace Sia.RenderGraph;

public sealed class RenderGraphPassBuilder
{
    private readonly RenderGraphBuilder _graph;

    internal RenderGraphPassBuilder(
        RenderGraphBuilder graph,
        RenderGraphPassHandle handle)
    {
        _graph = graph;
        Handle = handle;
    }

    public RenderGraphPassHandle Handle { get; }

    public RenderGraphPassBuilder Read(
        RenderGraphBufferHandle buffer,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range = default) =>
        Add(buffer, RenderGraphAccess.Read, usage, range);

    public RenderGraphPassBuilder Write(
        RenderGraphBufferHandle buffer,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range = default) =>
        Add(buffer, RenderGraphAccess.Write, usage, range);

    public RenderGraphPassBuilder ReadWrite(
        RenderGraphBufferHandle buffer,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range = default) =>
        Add(buffer, RenderGraphAccess.ReadWrite, usage, range);

    public RenderGraphPassBuilder Read(
        RenderGraphTextureHandle texture,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources = default) =>
        Add(texture, RenderGraphAccess.Read, usage, subresources);

    public RenderGraphPassBuilder Write(
        RenderGraphTextureHandle texture,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources = default) =>
        Add(texture, RenderGraphAccess.Write, usage, subresources);

    public RenderGraphPassBuilder ReadWrite(
        RenderGraphTextureHandle texture,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources = default) =>
        Add(texture, RenderGraphAccess.ReadWrite, usage, subresources);

    public RenderGraphPassBuilder DependsOn(RenderGraphPassHandle dependency)
    {
        _graph.AddDependency(Handle, dependency);
        return this;
    }

    public RenderGraphPassBuilder MarkSideEffect()
    {
        _graph.MarkSideEffect(Handle);
        return this;
    }

    private RenderGraphPassBuilder Add(
        RenderGraphBufferHandle buffer,
        RenderGraphAccess access,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range)
    {
        _graph.AddAccess(Handle, buffer, access, usage, range);
        return this;
    }

    private RenderGraphPassBuilder Add(
        RenderGraphTextureHandle texture,
        RenderGraphAccess access,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources)
    {
        _graph.AddAccess(Handle, texture, access, usage, subresources);
        return this;
    }
}

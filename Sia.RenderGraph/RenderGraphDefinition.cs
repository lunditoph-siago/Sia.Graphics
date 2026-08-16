namespace Sia.RenderGraph;

public sealed class RenderGraphDefinition
{
    internal RenderGraphDefinition(
        int graphId,
        RenderGraphBufferDefinition[] buffers,
        RenderGraphTextureDefinition[] textures,
        RenderGraphPassDefinition[] passes)
    {
        GraphId = graphId;
        Buffers = buffers;
        Textures = textures;
        Passes = passes;
    }

    public int BufferCount => Buffers.Length;

    public int TextureCount => Textures.Length;

    public int ResourceCount => BufferCount + TextureCount;

    public int PassCount => Passes.Length;

    internal int GraphId { get; }

    internal RenderGraphBufferDefinition[] Buffers { get; }

    internal RenderGraphTextureDefinition[] Textures { get; }

    internal RenderGraphPassDefinition[] Passes { get; }
}

internal readonly record struct RenderGraphBufferDefinition(
    RenderGraphBufferDescriptor Descriptor,
    bool IsImported,
    bool IsExported,
    RenderGraphBufferUsage ExportUsage);

internal readonly record struct RenderGraphTextureDefinition(
    RenderGraphTextureDescriptor Descriptor,
    bool IsImported,
    bool IsExported,
    RenderGraphTextureUsage ExportUsage);

internal readonly record struct RenderGraphBufferUse(
    int BufferIndex,
    RenderGraphAccess Access,
    RenderGraphBufferUsage Usage,
    RenderGraphBufferRange Range);

internal readonly record struct RenderGraphTextureUse(
    int TextureIndex,
    RenderGraphAccess Access,
    RenderGraphTextureUsage Usage,
    RenderGraphTextureSubresourceRange Subresources);

internal sealed record RenderGraphPassDefinition(
    string Name,
    RenderGraphPassKind Kind,
    RenderGraphBufferUse[] Buffers,
    RenderGraphTextureUse[] Textures,
    int[] Dependencies);

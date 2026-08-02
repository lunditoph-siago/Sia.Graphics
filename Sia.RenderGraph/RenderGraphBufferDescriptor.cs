namespace Sia.RenderGraph;

public readonly record struct RenderGraphBufferDescriptor
{
    public RenderGraphBufferDescriptor(
        string name,
        ulong size,
        RenderGraphBufferUsage usage = RenderGraphBufferUsage.None)
    {
        Name = name;
        Size = size;
        Usage = usage;
    }

    public string Name { get; }

    public ulong Size { get; }

    public RenderGraphBufferUsage Usage { get; }
}

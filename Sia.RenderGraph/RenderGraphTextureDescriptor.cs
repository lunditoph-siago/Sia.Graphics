namespace Sia.RenderGraph;

public readonly record struct RenderGraphTextureDescriptor
{
    public RenderGraphTextureDescriptor(
        string name,
        RenderGraphTextureFormat format,
        uint width,
        uint height = 1,
        uint depthOrArrayLayers = 1,
        uint mipLevelCount = 1,
        uint sampleCount = 1,
        RenderGraphTextureDimension dimension = RenderGraphTextureDimension.D2,
        RenderGraphTextureUsage usage = RenderGraphTextureUsage.None)
    {
        Name = name;
        Format = format;
        Width = width;
        Height = height;
        DepthOrArrayLayers = depthOrArrayLayers;
        MipLevelCount = mipLevelCount;
        SampleCount = sampleCount;
        Dimension = dimension;
        Usage = usage;
    }

    public string Name { get; }

    public RenderGraphTextureFormat Format { get; }

    public uint Width { get; }

    public uint Height { get; }

    public uint DepthOrArrayLayers { get; }

    public uint MipLevelCount { get; }

    public uint SampleCount { get; }

    public RenderGraphTextureDimension Dimension { get; }

    public RenderGraphTextureUsage Usage { get; }
}

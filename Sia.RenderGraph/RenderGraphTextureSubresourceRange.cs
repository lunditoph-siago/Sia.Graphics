namespace Sia.RenderGraph;

public readonly record struct RenderGraphTextureSubresourceRange(
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseArrayLayer,
    uint ArrayLayerCount,
    RenderGraphTextureAspect Aspect = RenderGraphTextureAspect.All)
{
    public static RenderGraphTextureSubresourceRange Whole => default;
}

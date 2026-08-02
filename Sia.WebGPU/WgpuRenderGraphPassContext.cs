using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphPassContext
{
    private readonly IReadOnlyDictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> _buffers;
    private readonly IReadOnlyDictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> _textures;
    private readonly List<WgpuHandle<WGPUTextureView>> _views;

    internal WgpuRenderGraphPassContext(
        WgpuRenderGraphPlan plan,
        CompiledRenderGraphPass pass,
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        IReadOnlyDictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        IReadOnlyDictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        List<WgpuHandle<WGPUTextureView>> views)
    {
        Plan = plan;
        Pass = pass;
        CommandEncoder = commandEncoder;
        _buffers = buffers;
        _textures = textures;
        _views = views;
    }

    public WgpuRenderGraphPlan Plan { get; }

    public CompiledRenderGraphPass Pass { get; }

    public WgpuHandle<WGPUCommandEncoder> CommandEncoder { get; }

    public WgpuHandle<WGPUBuffer> GetBuffer(RenderGraphBufferHandle buffer)
    {
        if (!Pass.Buffers.Any(access => access.Buffer == buffer)) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' did not declare access to the buffer.",
                nameof(buffer));
        }

        return _buffers[buffer];
    }

    public WgpuHandle<WGPUTexture> GetTexture(RenderGraphTextureHandle texture)
    {
        if (!Pass.Textures.Any(access => access.Texture == texture)) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' did not declare access to the texture.",
                nameof(texture));
        }

        return _textures[texture];
    }

    public WgpuHandle<WGPUTextureView> GetTextureView(
        RenderGraphTextureHandle texture)
    {
        var accesses = Pass.Textures
            .Where(access => access.Texture == texture)
            .ToArray();
        if (accesses.Length != 1) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' must have exactly one access to infer a texture view range.",
                nameof(texture));
        }

        return GetTextureView(texture, accesses[0].Subresources);
    }

    public WgpuHandle<WGPUTextureView> GetTextureView(
        RenderGraphTextureHandle texture,
        RenderGraphTextureSubresourceRange subresources)
    {
        var resource = Plan.Graph.GetTexture(texture);
        var normalized = Normalize(resource.Descriptor, subresources);
        if (!Pass.Textures.Any(access =>
            access.Texture == texture && Contains(access.Subresources, normalized))) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' did not declare the requested texture subresources.",
                nameof(subresources));
        }

        var textureHandle = _textures[texture];
        var texturePlan = Plan.Textures.Single(
            item => item.Resource.Handle == texture);
        var descriptor = WGPUTextureViewDescriptor.Default;
        descriptor.Format = texturePlan.Format;
        descriptor.Dimension = GetViewDimension(
            resource.Descriptor.Dimension,
            normalized.ArrayLayerCount);
        descriptor.BaseMipLevel = normalized.BaseMipLevel;
        descriptor.MipLevelCount = normalized.MipLevelCount;
        descriptor.BaseArrayLayer = resource.Descriptor.Dimension ==
            RenderGraphTextureDimension.D3
                ? 0
                : normalized.BaseArrayLayer;
        descriptor.ArrayLayerCount = resource.Descriptor.Dimension ==
            RenderGraphTextureDimension.D3
                ? 1
                : normalized.ArrayLayerCount;
        descriptor.Aspect = Lower(normalized.Aspect);
        descriptor.Usage = WGPUTextureUsage.None;

        var view = Wgpu.CreateTextureView(textureHandle, in descriptor);
        if (view.IsNull) {
            throw new InvalidOperationException(
                $"WebGPU could not create a view for texture '{resource.Descriptor.Name}'.");
        }

        _views.Add(view);
        return view;
    }

    private static RenderGraphTextureSubresourceRange Normalize(
        RenderGraphTextureDescriptor descriptor,
        RenderGraphTextureSubresourceRange subresources)
    {
        if (subresources.BaseMipLevel >= descriptor.MipLevelCount ||
            subresources.BaseArrayLayer >= descriptor.DepthOrArrayLayers) {
            throw new ArgumentOutOfRangeException(nameof(subresources));
        }

        var mipLevelCount = subresources.MipLevelCount == 0
            ? descriptor.MipLevelCount - subresources.BaseMipLevel
            : subresources.MipLevelCount;
        var arrayLayerCount = subresources.ArrayLayerCount == 0
            ? descriptor.DepthOrArrayLayers - subresources.BaseArrayLayer
            : subresources.ArrayLayerCount;
        if (mipLevelCount > descriptor.MipLevelCount - subresources.BaseMipLevel ||
            arrayLayerCount > descriptor.DepthOrArrayLayers - subresources.BaseArrayLayer) {
            throw new ArgumentOutOfRangeException(nameof(subresources));
        }
        if (descriptor.Dimension == RenderGraphTextureDimension.D3 &&
            (subresources.BaseArrayLayer != 0 ||
             arrayLayerCount != descriptor.DepthOrArrayLayers)) {
            throw new ArgumentException(
                "3D texture views cannot select individual depth slices.",
                nameof(subresources));
        }
        if (!SupportsAspect(descriptor.Format, subresources.Aspect)) {
            throw new ArgumentException(
                "The requested aspect is not present in the texture format.",
                nameof(subresources));
        }

        return subresources with {
            MipLevelCount = mipLevelCount,
            ArrayLayerCount = arrayLayerCount
        };
    }

    private static bool Contains(
        RenderGraphTextureSubresourceRange declared,
        RenderGraphTextureSubresourceRange requested) =>
        requested.BaseMipLevel >= declared.BaseMipLevel &&
        requested.BaseMipLevel + requested.MipLevelCount <=
            declared.BaseMipLevel + declared.MipLevelCount &&
        requested.BaseArrayLayer >= declared.BaseArrayLayer &&
        requested.BaseArrayLayer + requested.ArrayLayerCount <=
            declared.BaseArrayLayer + declared.ArrayLayerCount &&
        (declared.Aspect == RenderGraphTextureAspect.All ||
         declared.Aspect == requested.Aspect);

    private static WGPUTextureViewDimension GetViewDimension(
        RenderGraphTextureDimension dimension,
        uint arrayLayerCount) =>
        dimension switch {
            RenderGraphTextureDimension.D1 => WGPUTextureViewDimension._1D,
            RenderGraphTextureDimension.D2 when arrayLayerCount > 1 =>
                WGPUTextureViewDimension._2DArray,
            RenderGraphTextureDimension.D2 => WGPUTextureViewDimension._2D,
            RenderGraphTextureDimension.D3 => WGPUTextureViewDimension._3D,
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };

    private static WGPUTextureAspect Lower(RenderGraphTextureAspect aspect) =>
        aspect switch {
            RenderGraphTextureAspect.All => WGPUTextureAspect.All,
            RenderGraphTextureAspect.DepthOnly => WGPUTextureAspect.DepthOnly,
            RenderGraphTextureAspect.StencilOnly => WGPUTextureAspect.StencilOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(aspect))
        };

    private static bool SupportsAspect(
        RenderGraphTextureFormat format,
        RenderGraphTextureAspect aspect) =>
        format switch {
            RenderGraphTextureFormat.Stencil8 =>
                aspect is RenderGraphTextureAspect.All or
                    RenderGraphTextureAspect.StencilOnly,
            RenderGraphTextureFormat.Depth16Unorm or
            RenderGraphTextureFormat.Depth24Plus or
            RenderGraphTextureFormat.Depth32Float =>
                aspect is RenderGraphTextureAspect.All or
                    RenderGraphTextureAspect.DepthOnly,
            RenderGraphTextureFormat.Depth24PlusStencil8 or
            RenderGraphTextureFormat.Depth32FloatStencil8 => true,
            _ => aspect == RenderGraphTextureAspect.All
        };
}

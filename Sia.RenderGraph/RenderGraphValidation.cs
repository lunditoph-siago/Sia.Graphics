namespace Sia.RenderGraph;

[Flags]
internal enum RenderGraphFormatAspects : byte
{
    None = 0,
    Color = 1,
    Depth = 2,
    Stencil = 4
}

internal static class RenderGraphValidation
{
    private const RenderGraphBufferUsage AllBufferUsage =
        RenderGraphBufferUsage.MapRead |
        RenderGraphBufferUsage.MapWrite |
        RenderGraphBufferUsage.CopySource |
        RenderGraphBufferUsage.CopyDestination |
        RenderGraphBufferUsage.Index |
        RenderGraphBufferUsage.Vertex |
        RenderGraphBufferUsage.Uniform |
        RenderGraphBufferUsage.Storage |
        RenderGraphBufferUsage.Indirect |
        RenderGraphBufferUsage.QueryResolve;

    private const RenderGraphTextureUsage AllTextureUsage =
        RenderGraphTextureUsage.CopySource |
        RenderGraphTextureUsage.CopyDestination |
        RenderGraphTextureUsage.TextureBinding |
        RenderGraphTextureUsage.StorageBinding |
        RenderGraphTextureUsage.RenderAttachment |
        RenderGraphTextureUsage.TransientAttachment;

    public static void Validate(RenderGraphBufferDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        if (descriptor.Size == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                "A render graph buffer must have a non-zero size.");
        }

        Validate(descriptor.Usage, nameof(descriptor));
    }

    public static void Validate(RenderGraphTextureDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        if (descriptor.Format == RenderGraphTextureFormat.Undefined ||
            !Enum.IsDefined(descriptor.Format))
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                "A render graph texture must have a defined format.");
        }
        if (descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.DepthOrArrayLayers == 0 ||
            descriptor.MipLevelCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                "Texture dimensions and mip level count must be non-zero.");
        }
        if (descriptor.SampleCount is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                "Texture sample count must be 1 or 4.");
        }
        if (descriptor.Dimension == RenderGraphTextureDimension.D1 &&
            (descriptor.Height != 1 ||
             descriptor.DepthOrArrayLayers != 1 ||
             descriptor.SampleCount != 1))
        {
            throw new ArgumentException(
                "A 1D texture must have height, layer count, and sample count equal to 1.",
                nameof(descriptor));
        }
        if (descriptor.Dimension == RenderGraphTextureDimension.D3 &&
            descriptor.SampleCount != 1)
        {
            throw new ArgumentException(
                "A 3D texture cannot be multisampled.",
                nameof(descriptor));
        }
        if (descriptor.SampleCount > 1 &&
            (descriptor.Dimension != RenderGraphTextureDimension.D2 ||
             descriptor.DepthOrArrayLayers != 1 ||
             descriptor.MipLevelCount != 1))
        {
            throw new ArgumentException(
                "A multisampled texture must be a single-layer 2D texture with one mip level.",
                nameof(descriptor));
        }

        var maximumMipLevelCount = GetMaximumMipLevelCount(descriptor);
        if (descriptor.MipLevelCount > maximumMipLevelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                $"Texture mip level count exceeds the maximum {maximumMipLevelCount}.");
        }

        Validate(descriptor.Usage, nameof(descriptor));
    }

    public static void Validate(RenderGraphBufferUsage usage, string parameterName)
    {
        if ((usage & ~AllBufferUsage) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Buffer usage contains unknown flags.");
        }
    }

    public static void Validate(RenderGraphTextureUsage usage, string parameterName)
    {
        if ((usage & ~AllTextureUsage) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Texture usage contains unknown flags.");
        }
    }

    public static RenderGraphBufferRange Normalize(
        RenderGraphBufferDescriptor descriptor,
        RenderGraphBufferRange range)
    {
        if (range.Offset >= descriptor.Size)
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                "Buffer range starts outside the buffer.");
        }

        var size = range.Size == 0
            ? descriptor.Size - range.Offset
            : range.Size;
        if (size > descriptor.Size - range.Offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                "Buffer range extends beyond the buffer.");
        }

        return new RenderGraphBufferRange(range.Offset, size);
    }

    public static RenderGraphTextureSubresourceRange Normalize(
        RenderGraphTextureDescriptor descriptor,
        RenderGraphTextureSubresourceRange subresources)
    {
        if (subresources.BaseMipLevel >= descriptor.MipLevelCount ||
            subresources.BaseArrayLayer >= descriptor.DepthOrArrayLayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subresources),
                "Texture subresource range starts outside the texture.");
        }

        var mipLevelCount = subresources.MipLevelCount == 0
            ? descriptor.MipLevelCount - subresources.BaseMipLevel
            : subresources.MipLevelCount;
        var arrayLayerCount = subresources.ArrayLayerCount == 0
            ? descriptor.DepthOrArrayLayers - subresources.BaseArrayLayer
            : subresources.ArrayLayerCount;
        if (mipLevelCount > descriptor.MipLevelCount - subresources.BaseMipLevel ||
            arrayLayerCount > descriptor.DepthOrArrayLayers - subresources.BaseArrayLayer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subresources),
                "Texture subresource range extends beyond the texture.");
        }
        if (descriptor.Dimension == RenderGraphTextureDimension.D3 &&
            (subresources.BaseArrayLayer != 0 ||
             arrayLayerCount != descriptor.DepthOrArrayLayers))
        {
            throw new ArgumentException(
                "3D texture accesses may select mip levels but not individual depth slices.",
                nameof(subresources));
        }

        var availableAspects = GetFormatAspects(descriptor.Format);
        var requestedAspects = GetRequestedAspects(
            availableAspects,
            subresources.Aspect);
        if (requestedAspects == RenderGraphFormatAspects.None)
        {
            throw new ArgumentException(
                "The selected aspect is not present in the texture format.",
                nameof(subresources));
        }

        return subresources with {
            MipLevelCount = mipLevelCount,
            ArrayLayerCount = arrayLayerCount
        };
    }

    public static bool Overlaps(
        RenderGraphBufferRange first,
        RenderGraphBufferRange second) =>
        first.Offset < second.Offset + second.Size &&
        second.Offset < first.Offset + first.Size;

    public static bool Overlaps(
        RenderGraphTextureFormat format,
        RenderGraphTextureSubresourceRange first,
        RenderGraphTextureSubresourceRange second)
    {
        var mipsOverlap =
            first.BaseMipLevel < second.BaseMipLevel + second.MipLevelCount &&
            second.BaseMipLevel < first.BaseMipLevel + first.MipLevelCount;
        var layersOverlap =
            first.BaseArrayLayer < second.BaseArrayLayer + second.ArrayLayerCount &&
            second.BaseArrayLayer < first.BaseArrayLayer + first.ArrayLayerCount;
        var availableAspects = GetFormatAspects(format);
        var aspectsOverlap =
            (GetRequestedAspects(availableAspects, first.Aspect) &
             GetRequestedAspects(availableAspects, second.Aspect)) != 0;
        return mipsOverlap && layersOverlap && aspectsOverlap;
    }

    public static RenderGraphFormatAspects GetFormatAspects(
        RenderGraphTextureFormat format) =>
        format switch {
            RenderGraphTextureFormat.Stencil8 => RenderGraphFormatAspects.Stencil,
            RenderGraphTextureFormat.Depth16Unorm or
            RenderGraphTextureFormat.Depth24Plus or
            RenderGraphTextureFormat.Depth32Float => RenderGraphFormatAspects.Depth,
            RenderGraphTextureFormat.Depth24PlusStencil8 or
            RenderGraphTextureFormat.Depth32FloatStencil8 =>
                RenderGraphFormatAspects.Depth | RenderGraphFormatAspects.Stencil,
            _ => RenderGraphFormatAspects.Color
        };

    public static RenderGraphFormatAspects GetRequestedAspects(
        RenderGraphFormatAspects available,
        RenderGraphTextureAspect aspect) =>
        aspect switch {
            RenderGraphTextureAspect.All => available,
            RenderGraphTextureAspect.DepthOnly =>
                available & RenderGraphFormatAspects.Depth,
            RenderGraphTextureAspect.StencilOnly =>
                available & RenderGraphFormatAspects.Stencil,
            _ => RenderGraphFormatAspects.None
        };

    private static uint GetMaximumMipLevelCount(
        RenderGraphTextureDescriptor descriptor)
    {
        var largestDimension = descriptor.Dimension switch {
            RenderGraphTextureDimension.D1 => descriptor.Width,
            RenderGraphTextureDimension.D2 => Math.Max(
                descriptor.Width,
                descriptor.Height),
            RenderGraphTextureDimension.D3 => Math.Max(
                Math.Max(descriptor.Width, descriptor.Height),
                descriptor.DepthOrArrayLayers),
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
        };

        var count = 0u;
        do
        {
            count++;
            largestDimension >>= 1;
        }
        while (largestDimension != 0);

        return count;
    }
}

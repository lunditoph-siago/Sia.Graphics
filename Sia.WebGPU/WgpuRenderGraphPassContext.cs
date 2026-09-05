using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphPassContext
{
    private IReadOnlyDictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> _buffers;
    private IReadOnlyDictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> _textures;
    private WgpuRenderGraphViewCache _viewCache;
    private List<WgpuHandle<WGPUTextureView>> _transientViews;
    private WgpuRenderGraphGroupRenderPassState _groupRenderPass;

    internal WgpuRenderGraphPassContext(
        WgpuRenderGraphPlan plan,
        CompiledRenderGraphPass pass,
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        IReadOnlyDictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        IReadOnlyDictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        WgpuRenderGraphViewCache viewCache,
        List<WgpuHandle<WGPUTextureView>> transientViews,
        WgpuRenderGraphGroupRenderPassState groupRenderPass)
    {
        Plan = plan;
        Pass = pass;
        CommandEncoder = commandEncoder;
        _buffers = buffers;
        _textures = textures;
        _viewCache = viewCache;
        _transientViews = transientViews;
        _groupRenderPass = groupRenderPass;
    }

    internal void Reset(
        WgpuRenderGraphPlan plan,
        CompiledRenderGraphPass pass,
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        IReadOnlyDictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        IReadOnlyDictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        WgpuRenderGraphViewCache viewCache,
        List<WgpuHandle<WGPUTextureView>> transientViews,
        WgpuRenderGraphGroupRenderPassState groupRenderPass)
    {
        Plan = plan;
        Pass = pass;
        CommandEncoder = commandEncoder;
        _buffers = buffers;
        _textures = textures;
        _viewCache = viewCache;
        _transientViews = transientViews;
        _groupRenderPass = groupRenderPass;
    }

    public WgpuRenderGraphPlan Plan { get; private set; }

    public CompiledRenderGraphPass Pass { get; private set; }

    public WgpuHandle<WGPUCommandEncoder> CommandEncoder { get; private set; }

    public WgpuHandle<WGPUBuffer> GetBuffer(RenderGraphBufferHandle buffer)
    {
        if (!ContainsBuffer(buffer)) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' did not declare access to the buffer.",
                nameof(buffer));
        }

        return _buffers[buffer];
    }

    public WgpuHandle<WGPUTexture> GetTexture(RenderGraphTextureHandle texture)
    {
        if (!ContainsTexture(texture)) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' did not declare access to the texture.",
                nameof(texture));
        }

        return _textures[texture];
    }

    public WgpuHandle<WGPUTextureView> GetTextureView(
        RenderGraphTextureHandle texture,
        bool cacheable = true)
    {
        var found = default(RenderGraphTextureAccess);
        var matchCount = 0;
        var accesses = Pass.Textures;
        for (var index = 0; index < accesses.Count; index++) {
            var access = accesses[index];
            if (access.Texture != texture) {
                continue;
            }
            found = access;
            matchCount++;
        }
        if (matchCount != 1) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' must have exactly one access to infer a texture view range.",
                nameof(texture));
        }

        return GetTextureView(texture, found.Subresources, cacheable);
    }

    private bool ContainsBuffer(RenderGraphBufferHandle buffer)
    {
        var accesses = Pass.Buffers;
        for (var index = 0; index < accesses.Count; index++) {
            if (accesses[index].Buffer == buffer) {
                return true;
            }
        }
        return false;
    }

    private bool ContainsTexture(RenderGraphTextureHandle texture)
    {
        var accesses = Pass.Textures;
        for (var index = 0; index < accesses.Count; index++) {
            if (accesses[index].Texture == texture) {
                return true;
            }
        }
        return false;
    }

    public WgpuHandle<WGPUTextureView> GetTextureView(
        RenderGraphTextureHandle texture,
        RenderGraphTextureSubresourceRange subresources,
        bool cacheable = true)
    {
        var resource = Plan.Graph.GetTexture(texture);
        var normalized = Normalize(resource.Descriptor, subresources);
        if (!DeclaresSubresources(texture, normalized)) {
            throw new ArgumentException(
                $"Pass '{Pass.Name}' did not declare the requested texture subresources.",
                nameof(subresources));
        }

        var textureHandle = _textures[texture];
        var texturePlan = FindTexturePlan(texture);
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

        if (!cacheable) {
            var view = Wgpu.CreateTextureView(textureHandle, in descriptor);
            if (view.IsNull) {
                throw new InvalidOperationException(
                    $"WebGPU could not create a view for texture '{resource.Descriptor.Name}'.");
            }
            _transientViews.Add(view);
            return view;
        }

        return _viewCache.GetOrCreate(textureHandle, in descriptor);
    }

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        WgpuRenderGraphColorAttachment colorAttachment) =>
        GetOrBeginRenderPass(colorAttachment, depthStencilAttachment: null);

    public unsafe WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        WgpuRenderGraphColorAttachment colorAttachment,
        WgpuRenderGraphDepthStencilAttachment? depthStencilAttachment)
    {
        Span<WgpuRenderGraphColorAttachment> single = stackalloc WgpuRenderGraphColorAttachment[1];
        single[0] = colorAttachment;
        return GetOrBeginRenderPass(single, depthStencilAttachment);
    }

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        WgpuRenderGraphDepthStencilAttachment depthStencilAttachment) =>
        GetOrBeginRenderPass(ReadOnlySpan<WgpuRenderGraphColorAttachment>.Empty, depthStencilAttachment);

    public WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        ReadOnlySpan<WgpuRenderGraphColorAttachment> colorAttachments) =>
        GetOrBeginRenderPass(colorAttachments, depthStencilAttachment: null);

    public unsafe WgpuHandle<WGPURenderPassEncoder> GetOrBeginRenderPass(
        ReadOnlySpan<WgpuRenderGraphColorAttachment> colorAttachments,
        WgpuRenderGraphDepthStencilAttachment? depthStencilAttachment)
    {
        if (Pass.Kind != RenderGraphPassKind.Render) {
            throw new InvalidOperationException($"Pass '{Pass.Name}' was not declared as a render pass.");
        }
        if (colorAttachments.IsEmpty && depthStencilAttachment is null) {
            throw new ArgumentException(
                "At least one color attachment or a depth-stencil attachment is required to " +
                "begin a render pass.",
                nameof(colorAttachments));
        }

        var lowered =
            colorAttachments.Length <= 8
                ? stackalloc WGPURenderPassColorAttachment[colorAttachments.Length]
                : new WGPURenderPassColorAttachment[colorAttachments.Length];
        for (var index = 0; index < colorAttachments.Length; index++) {
            var attachment = colorAttachments[index];
            ValidateRenderAttachment(attachment.Texture, attachment.Subresources);
            var view = GetTextureView(
                attachment.Texture, attachment.Subresources, attachment.Cacheable);
            lowered[index] = WGPURenderPassColorAttachment.Default;
            lowered[index].View = (WGPUTextureView*)view.DangerousGetHandle();
            lowered[index].LoadOp = attachment.LoadOp;
            lowered[index].StoreOp = attachment.StoreOp;
            lowered[index].ClearValue = attachment.ClearValue;
        }

        var loweredDepthStencil = WGPURenderPassDepthStencilAttachment.Default;
        if (depthStencilAttachment is { } depthStencil) {
            ValidateRenderAttachment(depthStencil.Texture, depthStencil.Subresources);
            var view = GetTextureView(
                depthStencil.Texture, depthStencil.Subresources, depthStencil.Cacheable);
            loweredDepthStencil.View = (WGPUTextureView*)view.DangerousGetHandle();
            loweredDepthStencil.DepthLoadOp = depthStencil.DepthLoadOp;
            loweredDepthStencil.DepthStoreOp = depthStencil.DepthStoreOp;
            loweredDepthStencil.DepthClearValue = depthStencil.DepthClearValue;
            loweredDepthStencil.DepthReadOnly =
                depthStencil.DepthReadOnly ? WgpuConstants.True : WgpuConstants.False;
            loweredDepthStencil.StencilLoadOp = depthStencil.StencilLoadOp;
            loweredDepthStencil.StencilStoreOp = depthStencil.StencilStoreOp;
            loweredDepthStencil.StencilClearValue = depthStencil.StencilClearValue;
            loweredDepthStencil.StencilReadOnly =
                depthStencil.StencilReadOnly ? WgpuConstants.True : WgpuConstants.False;
        }

        if (_groupRenderPass.CanReuse(Pass.Handle, colorAttachments, depthStencilAttachment)) {
            return _groupRenderPass.Encoder;
        }
        _groupRenderPass.End();

        fixed (WGPURenderPassColorAttachment* attachmentsPtr = lowered) {
            var descriptor = WGPURenderPassDescriptor.Default;
            descriptor.ColorAttachmentCount = (uint)lowered.Length;
            descriptor.ColorAttachments = lowered.IsEmpty ? null : attachmentsPtr;
            if (depthStencilAttachment is not null) {
                descriptor.DepthStencilAttachment = &loweredDepthStencil;
            }
            var encoder = Wgpu.BeginRenderPass(CommandEncoder, in descriptor);
            if (encoder.IsNull) {
                throw new InvalidOperationException(
                    $"WebGPU could not begin the render pass for pass '{Pass.Name}'.");
            }
            _groupRenderPass.SetEncoder(encoder, Pass.Handle, colorAttachments, depthStencilAttachment);
            return encoder;
        }
    }

    public WgpuHandle<WGPUComputePassEncoder> GetOrBeginComputePass()
    {
        if (Pass.Kind != RenderGraphPassKind.Compute) {
            throw new InvalidOperationException(
                $"Pass '{Pass.Name}' was not declared as a compute pass.");
        }

        var computePass = Wgpu.BeginComputePass(CommandEncoder);
        if (computePass.IsNull) {
            throw new InvalidOperationException(
                $"WebGPU could not begin the compute pass for pass '{Pass.Name}'.");
        }
        return computePass;
    }

    private void ValidateRenderAttachment(RenderGraphTextureHandle texture, RenderGraphTextureSubresourceRange subresources)
    {
        var normalized = Normalize(Plan.Graph.GetTexture(texture).Descriptor, subresources);
        foreach (var access in Pass.Textures) {
            if (access.Texture == texture &&
                (access.Usage & RenderGraphTextureUsage.RenderAttachment) != 0 &&
                Contains(access.Subresources, normalized)) {
                return;
            }
        }
        throw new ArgumentException($"Pass '{Pass.Name}' did not declare the requested render attachment.", nameof(texture));
    }

    private bool DeclaresSubresources(
        RenderGraphTextureHandle texture,
        RenderGraphTextureSubresourceRange normalized)
    {
        var accesses = Pass.Textures;
        for (var index = 0; index < accesses.Count; index++) {
            var access = accesses[index];
            if (access.Texture == texture && Contains(access.Subresources, normalized)) {
                return true;
            }
        }
        return false;
    }

    private WgpuRenderGraphTexturePlan FindTexturePlan(RenderGraphTextureHandle texture)
    {
        var textures = Plan.Textures;
        for (var index = 0; index < textures.Count; index++) {
            if (textures[index].Resource.Handle == texture) {
                return textures[index];
            }
        }
        throw new ArgumentException(
            "Render graph texture is not part of the current plan.", nameof(texture));
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

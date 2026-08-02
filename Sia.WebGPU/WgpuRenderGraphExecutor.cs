using Sia.RenderGraph;

namespace Sia.WebGPU;

public static class WgpuRenderGraphExecutor
{
    public static WgpuRenderGraphExports Execute(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(plan, bindings.Plan)) {
            throw new ArgumentException(
                "The bindings were created for a different WebGPU render graph plan.",
                nameof(bindings));
        }
        if (device.IsNull) {
            throw new ArgumentException("The WebGPU device is null.", nameof(device));
        }
        if (queue.IsNull) {
            throw new ArgumentException("The WebGPU queue is null.", nameof(queue));
        }

        var buffers = new Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>>();
        var textures = new Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>>();
        var ownedBuffers = new HashSet<RenderGraphBufferHandle>();
        var ownedTextures = new HashSet<RenderGraphTextureHandle>();
        var views = new List<WgpuHandle<WGPUTextureView>>();
        var commandEncoder = default(WgpuHandle<WGPUCommandEncoder>);
        var commandBuffer = default(WgpuHandle<WGPUCommandBuffer>);

        try {
            CreateBuffers(plan, device, bindings, buffers, ownedBuffers);
            CreateTextures(plan, device, bindings, textures, ownedTextures);

            if (plan.Graph.Passes.Count != 0) {
                commandEncoder = Wgpu.CreateCommandEncoder(device);
                if (commandEncoder.IsNull) {
                    throw new InvalidOperationException(
                        "WebGPU could not create the render graph command encoder.");
                }

                foreach (var pass in plan.Graph.Passes) {
                    if (!bindings.TryGetHandler(pass.Handle, out var handler) ||
                        handler is null) {
                        continue;
                    }

                    var context = new WgpuRenderGraphPassContext(
                        plan,
                        pass,
                        commandEncoder,
                        buffers,
                        textures,
                        views);
                    handler(context);
                }

                var descriptor = WGPUCommandBufferDescriptor.Default;
                commandBuffer = Wgpu.FinishCommandEncoder(
                    commandEncoder,
                    in descriptor);
                if (commandBuffer.IsNull) {
                    throw new InvalidOperationException(
                        "WebGPU could not finish the render graph command encoder.");
                }

                Wgpu.Submit(queue, [commandBuffer]);
            }

            var exports = new WgpuRenderGraphExports(plan);
            TransferExports(
                plan,
                buffers,
                textures,
                ownedBuffers,
                ownedTextures,
                exports);
            return exports;
        }
        finally {
            for (var index = views.Count - 1; index >= 0; index--) {
                var view = views[index];
                Wgpu.Release(ref view);
            }
            Wgpu.Release(ref commandBuffer);
            Wgpu.Release(ref commandEncoder);
            ReleaseBuffers(buffers, ownedBuffers);
            ReleaseTextures(textures, ownedTextures);
        }
    }

    private static void CreateBuffers(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuRenderGraphBindings bindings,
        Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        HashSet<RenderGraphBufferHandle> ownedBuffers)
    {
        foreach (var item in plan.Buffers) {
            var resource = item.Resource;
            if (!resource.Lifetime.IsUsed) {
                continue;
            }

            if (resource.IsImported) {
                if (!bindings.TryGetBuffer(resource.Handle, out var imported)) {
                    throw new InvalidOperationException(
                        $"Imported buffer '{resource.Descriptor.Name}' has not been bound.");
                }

                ValidateImported(resource, item.Usage, imported);
                buffers.Add(resource.Handle, imported);
                continue;
            }

            using var label = WgpuOwnedString.Create(resource.Descriptor.Name);
            var descriptor = WGPUBufferDescriptor.Default;
            descriptor.Label = label.View;
            descriptor.Size = resource.Descriptor.Size;
            descriptor.Usage = item.Usage;
            var buffer = Wgpu.CreateBuffer(device, in descriptor);
            if (buffer.IsNull) {
                throw new InvalidOperationException(
                    $"WebGPU could not create buffer '{resource.Descriptor.Name}'.");
            }

            buffers.Add(resource.Handle, buffer);
            ownedBuffers.Add(resource.Handle);
        }
    }

    private static void CreateTextures(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuRenderGraphBindings bindings,
        Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        HashSet<RenderGraphTextureHandle> ownedTextures)
    {
        foreach (var item in plan.Textures) {
            var resource = item.Resource;
            if (!resource.Lifetime.IsUsed) {
                continue;
            }

            if (resource.IsImported) {
                if (!bindings.TryGetTexture(resource.Handle, out var imported)) {
                    throw new InvalidOperationException(
                        $"Imported texture '{resource.Descriptor.Name}' has not been bound.");
                }

                ValidateImported(resource, item, imported);
                textures.Add(resource.Handle, imported);
                continue;
            }

            using var label = WgpuOwnedString.Create(resource.Descriptor.Name);
            var descriptor = WGPUTextureDescriptor.Default;
            descriptor.Label = label.View;
            descriptor.Usage = item.Usage;
            descriptor.Dimension = item.Dimension;
            descriptor.Size = new WGPUExtent3D {
                Width = resource.Descriptor.Width,
                Height = resource.Descriptor.Height,
                DepthOrArrayLayers = resource.Descriptor.DepthOrArrayLayers,
            };
            descriptor.Format = item.Format;
            descriptor.MipLevelCount = resource.Descriptor.MipLevelCount;
            descriptor.SampleCount = resource.Descriptor.SampleCount;
            var texture = Wgpu.CreateTexture(device, in descriptor);
            if (texture.IsNull) {
                throw new InvalidOperationException(
                    $"WebGPU could not create texture '{resource.Descriptor.Name}'.");
            }

            textures.Add(resource.Handle, texture);
            ownedTextures.Add(resource.Handle);
        }
    }

    private static void ValidateImported(
        CompiledRenderGraphBuffer resource,
        WGPUBufferUsage usage,
        WgpuHandle<WGPUBuffer> handle)
    {
        var actual = Wgpu.GetBufferInfo(handle);
        if (actual.Size < resource.Descriptor.Size ||
            (actual.Usage & usage) != usage) {
            throw new InvalidOperationException(
                $"Imported buffer '{resource.Descriptor.Name}' does not match the compiled render graph descriptor.");
        }
    }

    private static void ValidateImported(
        CompiledRenderGraphTexture resource,
        WgpuRenderGraphTexturePlan plan,
        WgpuHandle<WGPUTexture> handle)
    {
        var actual = Wgpu.GetTextureInfo(handle);
        var descriptor = resource.Descriptor;
        if (actual.Size.Width != descriptor.Width ||
            actual.Size.Height != descriptor.Height ||
            actual.Size.DepthOrArrayLayers != descriptor.DepthOrArrayLayers ||
            actual.Dimension != plan.Dimension ||
            actual.Format != plan.Format ||
            actual.MipLevelCount != descriptor.MipLevelCount ||
            actual.SampleCount != descriptor.SampleCount ||
            (actual.Usage & plan.Usage) != plan.Usage) {
            throw new InvalidOperationException(
                $"Imported texture '{descriptor.Name}' does not match the compiled render graph descriptor.");
        }
    }

    private static void TransferExports(
        WgpuRenderGraphPlan plan,
        Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        HashSet<RenderGraphBufferHandle> ownedBuffers,
        HashSet<RenderGraphTextureHandle> ownedTextures,
        WgpuRenderGraphExports exports)
    {
        foreach (var item in plan.Buffers.Where(static item => item.Resource.IsExported)) {
            var handle = buffers[item.Resource.Handle];
            var ownsHandle = ownedBuffers.Remove(item.Resource.Handle);
            exports.Add(item.Resource.Handle, handle, ownsHandle);
        }
        foreach (var item in plan.Textures.Where(static item => item.Resource.IsExported)) {
            var handle = textures[item.Resource.Handle];
            var ownsHandle = ownedTextures.Remove(item.Resource.Handle);
            exports.Add(item.Resource.Handle, handle, ownsHandle);
        }
    }

    private static void ReleaseBuffers(
        Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        HashSet<RenderGraphBufferHandle> ownedBuffers)
    {
        foreach (var buffer in ownedBuffers) {
            var handle = buffers[buffer];
            Wgpu.Release(ref handle);
        }
    }

    private static void ReleaseTextures(
        Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        HashSet<RenderGraphTextureHandle> ownedTextures)
    {
        foreach (var texture in ownedTextures) {
            var handle = textures[texture];
            Wgpu.Release(ref handle);
        }
    }
}

using Sia.RenderGraph;

namespace Sia.WebGPU;

public static class WgpuRenderGraphExecutor
{
    public static WgpuRenderGraphExports Execute(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphExecutionScratch scratch) =>
        Execute(plan, device, queue, bindings, viewCache, scratch, resourcePool: null, out _);

    public static WgpuRenderGraphExports Execute(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphExecutionScratch scratch,
        WgpuRenderGraphResourcePool resourcePool) =>
        Execute(plan, device, queue, bindings, viewCache, scratch, resourcePool, out _);

    public static WgpuRenderGraphExports Execute(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphExecutionScratch scratch,
        out int physicalRenderPassCount)
        => Execute(
            plan,
            device,
            queue,
            bindings,
            viewCache,
            scratch,
            resourcePool: null,
            out physicalRenderPassCount);

    public static WgpuRenderGraphExports Execute(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphExecutionScratch scratch,
        WgpuRenderGraphResourcePool? resourcePool,
        out int physicalRenderPassCount)
    {
        return ExecuteCore(
            plan,
            device,
            queue,
            bindings,
            viewCache,
            scratch,
            resourcePool,
            captureExports: true,
            out physicalRenderPassCount)!;
    }

    public static void ExecuteWithoutExports(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphExecutionScratch scratch,
        out int physicalRenderPassCount)
        => ExecuteWithoutExports(
            plan,
            device,
            queue,
            bindings,
            viewCache,
            scratch,
            resourcePool: null,
            out physicalRenderPassCount);

    public static void ExecuteWithoutExports(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphExecutionScratch scratch,
        WgpuRenderGraphResourcePool? resourcePool,
        out int physicalRenderPassCount)
    {
        _ = ExecuteCore(
            plan,
            device,
            queue,
            bindings,
            viewCache,
            scratch,
            resourcePool,
            captureExports: false,
            out physicalRenderPassCount);
    }

    private static WgpuRenderGraphExports? ExecuteCore(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue,
        WgpuRenderGraphBindings bindings,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphExecutionScratch scratch,
        WgpuRenderGraphResourcePool? resourcePool,
        bool captureExports,
        out int physicalRenderPassCount)
    {
        physicalRenderPassCount = 0;
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(viewCache);
        ArgumentNullException.ThrowIfNull(scratch);
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
        foreach (var pass in plan.Graph.Passes) {
            if (!bindings.TryGetHandler(pass.Handle, out var handler) || handler is null) {
                throw new InvalidOperationException($"Render graph pass '{pass.Name}' has no execution handler.");
            }
        }
        scratch.BeginExecution();
        var buffers = scratch._buffers;
        var textures = scratch._textures;
        var ownedBuffers = scratch._ownedBuffers;
        var ownedTextures = scratch._ownedTextures;
        var transientViews = scratch._transientViews;
        var commandEncoder = default(WgpuHandle<WGPUCommandEncoder>);
        var commandBuffer = default(WgpuHandle<WGPUCommandBuffer>);

        try {
            resourcePool?.BeginFrame(device);
            CreateBuffers(plan, device, bindings, buffers, ownedBuffers, resourcePool);
            CreateTextures(plan, device, bindings, textures, ownedTextures, resourcePool);

            if (plan.Graph.Passes.Count != 0) {
                commandEncoder = Wgpu.CreateCommandEncoder(device);
                if (commandEncoder.IsNull) {
                    throw new InvalidOperationException(
                        "WebGPU could not create the render graph command encoder.");
                }

                for (var groupIndex = 0;
                    groupIndex < plan.Graph.PassGroups.Count;
                    groupIndex++) {
                    var group = plan.Graph.PassGroups[groupIndex];
                    var groupRenderPass = scratch.RentGroupState();
                    try {
                        for (var offset = 0; offset < group.Count; offset++) {
                            var pass = plan.Graph.Passes[group.StartExecutionIndex + offset];
                            if (!bindings.TryGetHandler(pass.Handle, out var handler) || handler is null) {
                                throw new InvalidOperationException($"Render graph pass '{pass.Name}' has no execution handler.");
                            }

                            var context = scratch.RentPassContext(
                                plan,
                                pass,
                                commandEncoder,
                                viewCache,
                                groupRenderPass);
                            handler(context);
                        }
                    }
                    finally {
                        groupRenderPass.End();
                        physicalRenderPassCount += groupRenderPass.RenderPassCount;
                    }
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

            if (captureExports) {
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
            return null;
        }
        finally {
            try {
                for (var index = transientViews.Count - 1; index >= 0; index--) {
                    var view = transientViews[index];
                    Wgpu.Release(ref view);
                }
                viewCache.EndFrame();
                Wgpu.Release(ref commandBuffer);
                Wgpu.Release(ref commandEncoder);
                ReleaseBuffers(plan, buffers, ownedBuffers, resourcePool);
                ReleaseTextures(plan, textures, ownedTextures, resourcePool);
            }
            finally {
                scratch.EndExecution();
            }
        }
    }

    private static void CreateBuffers(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuRenderGraphBindings bindings,
        Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        HashSet<RenderGraphBufferHandle> ownedBuffers,
        WgpuRenderGraphResourcePool? resourcePool)
    {
        for (var index = 0; index < plan.Buffers.Count; index++) {
            var item = plan.Buffers[index];
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

            var buffer = resourcePool is null
                ? CreateBuffer(device, resource.Descriptor, item.Usage)
                : resourcePool.RentBuffer(device, resource.Descriptor, item.Usage);

            buffers.Add(resource.Handle, buffer);
            ownedBuffers.Add(resource.Handle);
        }
    }

    private static void CreateTextures(
        WgpuRenderGraphPlan plan,
        WgpuHandle<WGPUDevice> device,
        WgpuRenderGraphBindings bindings,
        Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        HashSet<RenderGraphTextureHandle> ownedTextures,
        WgpuRenderGraphResourcePool? resourcePool)
    {
        for (var index = 0; index < plan.Textures.Count; index++) {
            var item = plan.Textures[index];
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

            var texture = resourcePool is null
                ? CreateTexture(device, resource.Descriptor, item)
                : resourcePool.RentTexture(
                    device,
                    resource.Descriptor,
                    item.Dimension,
                    item.Format,
                    item.Usage);

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

    private static WgpuHandle<WGPUBuffer> CreateBuffer(
        WgpuHandle<WGPUDevice> device,
        in RenderGraphBufferDescriptor resource,
        WGPUBufferUsage usage)
    {
        using var label = WgpuOwnedString.Create(resource.Name);
        var descriptor = WGPUBufferDescriptor.Default;
        descriptor.Label = label.View;
        descriptor.Size = resource.Size;
        descriptor.Usage = usage;
        var buffer = Wgpu.CreateBuffer(device, in descriptor);
        if (buffer.IsNull) {
            throw new InvalidOperationException(
                $"WebGPU could not create buffer '{resource.Name}'.");
        }
        return buffer;
    }

    private static WgpuHandle<WGPUTexture> CreateTexture(
        WgpuHandle<WGPUDevice> device,
        in RenderGraphTextureDescriptor resource,
        WgpuRenderGraphTexturePlan plan)
    {
        using var label = WgpuOwnedString.Create(resource.Name);
        var descriptor = WGPUTextureDescriptor.Default;
        descriptor.Label = label.View;
        descriptor.Usage = plan.Usage;
        descriptor.Dimension = plan.Dimension;
        descriptor.Size = new WGPUExtent3D {
            Width = resource.Width,
            Height = resource.Height,
            DepthOrArrayLayers = resource.DepthOrArrayLayers,
        };
        descriptor.Format = plan.Format;
        descriptor.MipLevelCount = resource.MipLevelCount;
        descriptor.SampleCount = resource.SampleCount;
        var texture = Wgpu.CreateTexture(device, in descriptor);
        if (texture.IsNull) {
            throw new InvalidOperationException(
                $"WebGPU could not create texture '{resource.Name}'.");
        }
        return texture;
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
        for (var index = 0; index < plan.Buffers.Count; index++) {
            var item = plan.Buffers[index];
            if (!item.Resource.IsExported) {
                continue;
            }
            var handle = buffers[item.Resource.Handle];
            var ownsHandle = ownedBuffers.Remove(item.Resource.Handle);
            exports.Add(item.Resource.Handle, handle, ownsHandle);
        }
        for (var index = 0; index < plan.Textures.Count; index++) {
            var item = plan.Textures[index];
            if (!item.Resource.IsExported) {
                continue;
            }
            var handle = textures[item.Resource.Handle];
            var ownsHandle = ownedTextures.Remove(item.Resource.Handle);
            exports.Add(item.Resource.Handle, handle, ownsHandle);
        }
    }

    private static void ReleaseBuffers(
        WgpuRenderGraphPlan plan,
        Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> buffers,
        HashSet<RenderGraphBufferHandle> ownedBuffers,
        WgpuRenderGraphResourcePool? resourcePool)
    {
        foreach (var item in plan.Buffers) {
            var resource = item.Resource;
            if (!ownedBuffers.Contains(resource.Handle)) {
                continue;
            }
            var handle = buffers[resource.Handle];
            if (resourcePool is null) {
                Wgpu.Release(ref handle);
            }
            else {
                resourcePool.ReturnBuffer(resource.Descriptor, item.Usage, handle);
            }
        }
    }

    private static void ReleaseTextures(
        WgpuRenderGraphPlan plan,
        Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> textures,
        HashSet<RenderGraphTextureHandle> ownedTextures,
        WgpuRenderGraphResourcePool? resourcePool)
    {
        foreach (var item in plan.Textures) {
            var resource = item.Resource;
            if (!ownedTextures.Contains(resource.Handle)) {
                continue;
            }
            var handle = textures[resource.Handle];
            if (resourcePool is null) {
                Wgpu.Release(ref handle);
            }
            else {
                resourcePool.ReturnTexture(
                    resource.Descriptor,
                    item.Dimension,
                    item.Format,
                    item.Usage,
                    handle);
            }
        }
    }
}

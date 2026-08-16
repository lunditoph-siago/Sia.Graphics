namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static WgpuHandle<WGPUCommandEncoder> CreateCommandEncoder(
        WgpuHandle<WGPUDevice> device,
        in WGPUCommandEncoderDescriptor descriptor)
    {
        fixed (WGPUCommandEncoderDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUCommandEncoder>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateCommandEncoder(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUCommandEncoder> CreateCommandEncoder(WgpuHandle<WGPUDevice> device)
    {
        var descriptor = new WGPUCommandEncoderDescriptor {
            NextInChain = null,
            Label = default,
        };

        return CreateCommandEncoder(device, in descriptor);
    }

    public static WgpuHandle<WGPUCommandBuffer> FinishCommandEncoder(
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        in WGPUCommandBufferDescriptor descriptor)
    {
        fixed (WGPUCommandBufferDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUCommandBuffer>.FromPointer(
                WgpuUnsafe.wgpuCommandEncoderFinish(GetPointer(commandEncoder), descriptorPtr));
        }
    }

    public static void CopyBufferToBuffer(
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        WgpuHandle<WGPUBuffer> source,
        ulong sourceOffset,
        WgpuHandle<WGPUBuffer> destination,
        ulong destinationOffset,
        ulong size) =>
        WgpuUnsafe.wgpuCommandEncoderCopyBufferToBuffer(
            GetPointer(commandEncoder),
            GetPointer(source),
            sourceOffset,
            GetPointer(destination),
            destinationOffset,
            size);

    public static void CopyTextureToBuffer(
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        WgpuHandle<WGPUTexture> source,
        WgpuHandle<WGPUBuffer> destination,
        uint width,
        uint height,
        uint bytesPerRow,
        uint mipLevel = 0)
    {
        var sourceInfo = new WGPUTexelCopyTextureInfo {
            Texture = GetPointer(source),
            MipLevel = mipLevel,
            Origin = default,
            Aspect = WGPUTextureAspect.All
        };
        var destinationInfo = new WGPUTexelCopyBufferInfo {
            Layout = new WGPUTexelCopyBufferLayout {
                Offset = 0,
                BytesPerRow = bytesPerRow,
                RowsPerImage = height
            },
            Buffer = GetPointer(destination)
        };
        var extent = new WGPUExtent3D { Width = width, Height = height, DepthOrArrayLayers = 1 };
        WgpuUnsafe.wgpuCommandEncoderCopyTextureToBuffer(
            GetPointer(commandEncoder), &sourceInfo, &destinationInfo, &extent);
    }

    public static WgpuHandle<WGPURenderPassEncoder> BeginRenderPass(
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        in WGPURenderPassDescriptor descriptor)
    {
        fixed (WGPURenderPassDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPURenderPassEncoder>.FromPointer(
                WgpuUnsafe.wgpuCommandEncoderBeginRenderPass(GetPointer(commandEncoder), descriptorPtr));
        }
    }

    public static void SetRenderPipeline(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        WgpuHandle<WGPURenderPipeline> pipeline) =>
        WgpuUnsafe.wgpuRenderPassEncoderSetPipeline(GetPointer(renderPass), GetPointer(pipeline));

    public static void SetBindGroup(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        uint groupIndex,
        WgpuHandle<WGPUBindGroup> bindGroup,
        ReadOnlySpan<uint> dynamicOffsets = default)
    {
        fixed (uint* dynamicOffsetsPtr = dynamicOffsets) {
            WgpuUnsafe.wgpuRenderPassEncoderSetBindGroup(
                GetPointer(renderPass),
                groupIndex,
                GetPointer(bindGroup),
                (nuint)dynamicOffsets.Length,
                dynamicOffsetsPtr);
        }
    }

    public static void SetIndexBuffer(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        WgpuHandle<WGPUBuffer> buffer,
        WGPUIndexFormat format,
        ulong offset = 0,
        ulong? size = null)
    {
        var bufferSize = GetBufferSize(buffer);
        if (offset > bufferSize) {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var bindingSize = size ?? bufferSize - offset;
        if (bindingSize > bufferSize - offset) {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        WgpuUnsafe.wgpuRenderPassEncoderSetIndexBuffer(
            GetPointer(renderPass),
            GetPointer(buffer),
            format,
            offset,
            bindingSize);
    }

    public static void SetVertexBuffer(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        uint slot,
        WgpuHandle<WGPUBuffer> buffer,
        ulong offset = 0,
        ulong? size = null)
    {
        var bufferSize = GetBufferSize(buffer);
        if (offset > bufferSize) {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var bindingSize = size ?? bufferSize - offset;
        if (bindingSize > bufferSize - offset) {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        WgpuUnsafe.wgpuRenderPassEncoderSetVertexBuffer(
            GetPointer(renderPass),
            slot,
            GetPointer(buffer),
            offset,
            bindingSize);
    }

    public static void Draw(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        uint vertexCount,
        uint instanceCount = 1,
        uint firstVertex = 0,
        uint firstInstance = 0) =>
        WgpuUnsafe.wgpuRenderPassEncoderDraw(GetPointer(renderPass), vertexCount, instanceCount, firstVertex, firstInstance);

    public static void DrawIndexed(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        uint indexCount,
        uint instanceCount = 1,
        uint firstIndex = 0,
        int baseVertex = 0,
        uint firstInstance = 0) =>
        WgpuUnsafe.wgpuRenderPassEncoderDrawIndexed(
            GetPointer(renderPass), indexCount, instanceCount, firstIndex, baseVertex, firstInstance);

    public static void DrawIndirect(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        WgpuHandle<WGPUBuffer> indirectBuffer,
        ulong indirectOffset = 0) =>
        WgpuUnsafe.wgpuRenderPassEncoderDrawIndirect(
            GetPointer(renderPass), GetPointer(indirectBuffer), indirectOffset);

    public static void DrawIndexedIndirect(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        WgpuHandle<WGPUBuffer> indirectBuffer,
        ulong indirectOffset = 0) =>
        WgpuUnsafe.wgpuRenderPassEncoderDrawIndexedIndirect(
            GetPointer(renderPass), GetPointer(indirectBuffer), indirectOffset);

    public static void EndRenderPass(WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        WgpuUnsafe.wgpuRenderPassEncoderEnd(GetPointer(renderPass));

    public static WgpuHandle<WGPUComputePassEncoder> BeginComputePass(
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        in WGPUComputePassDescriptor descriptor)
    {
        fixed (WGPUComputePassDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUComputePassEncoder>.FromPointer(
                WgpuUnsafe.wgpuCommandEncoderBeginComputePass(GetPointer(commandEncoder), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUComputePassEncoder> BeginComputePass(
        WgpuHandle<WGPUCommandEncoder> commandEncoder) =>
        BeginComputePass(commandEncoder, WGPUComputePassDescriptor.Default);

    public static void SetComputePipeline(
        WgpuHandle<WGPUComputePassEncoder> computePass,
        WgpuHandle<WGPUComputePipeline> pipeline) =>
        WgpuUnsafe.wgpuComputePassEncoderSetPipeline(GetPointer(computePass), GetPointer(pipeline));

    public static void SetBindGroup(
        WgpuHandle<WGPUComputePassEncoder> computePass,
        uint groupIndex,
        WgpuHandle<WGPUBindGroup> bindGroup,
        ReadOnlySpan<uint> dynamicOffsets = default)
    {
        fixed (uint* dynamicOffsetsPtr = dynamicOffsets) {
            WgpuUnsafe.wgpuComputePassEncoderSetBindGroup(
                GetPointer(computePass),
                groupIndex,
                GetPointer(bindGroup),
                (nuint)dynamicOffsets.Length,
                dynamicOffsetsPtr);
        }
    }

    public static void DispatchWorkgroups(
        WgpuHandle<WGPUComputePassEncoder> computePass,
        uint workgroupCountX,
        uint workgroupCountY = 1,
        uint workgroupCountZ = 1) =>
        WgpuUnsafe.wgpuComputePassEncoderDispatchWorkgroups(
            GetPointer(computePass), workgroupCountX, workgroupCountY, workgroupCountZ);

    public static void DispatchWorkgroupsIndirect(
        WgpuHandle<WGPUComputePassEncoder> computePass,
        WgpuHandle<WGPUBuffer> indirectBuffer,
        ulong indirectOffset = 0) =>
        WgpuUnsafe.wgpuComputePassEncoderDispatchWorkgroupsIndirect(
            GetPointer(computePass), GetPointer(indirectBuffer), indirectOffset);

    public static void EndComputePass(WgpuHandle<WGPUComputePassEncoder> computePass) =>
        WgpuUnsafe.wgpuComputePassEncoderEnd(GetPointer(computePass));

    public static void Submit(
        WgpuHandle<WGPUQueue> queue,
        ReadOnlySpan<WgpuHandle<WGPUCommandBuffer>> commandBuffers)
    {
        if (commandBuffers.IsEmpty) {
            WgpuUnsafe.wgpuQueueSubmit(GetPointer(queue), 0, null);
            return;
        }

        const int stackLimit = 128;
        var nativeCommandBuffers = commandBuffers.Length <= stackLimit
            ? stackalloc nint[commandBuffers.Length]
            : new nint[commandBuffers.Length];

        for (var index = 0; index < commandBuffers.Length; index++) {
            nativeCommandBuffers[index] = (nint)GetPointer(commandBuffers[index]);
        }

        fixed (nint* nativeCommandBuffersPtr = nativeCommandBuffers) {
            WgpuUnsafe.wgpuQueueSubmit(
                GetPointer(queue),
                (nuint)commandBuffers.Length,
                (WGPUCommandBuffer**)nativeCommandBuffersPtr);
        }
    }
}

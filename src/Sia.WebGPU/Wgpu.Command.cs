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

    public static void Draw(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        uint vertexCount,
        uint instanceCount = 1,
        uint firstVertex = 0,
        uint firstInstance = 0) =>
        WgpuUnsafe.wgpuRenderPassEncoderDraw(GetPointer(renderPass), vertexCount, instanceCount, firstVertex, firstInstance);

    public static void EndRenderPass(WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        WgpuUnsafe.wgpuRenderPassEncoderEnd(GetPointer(renderPass));

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

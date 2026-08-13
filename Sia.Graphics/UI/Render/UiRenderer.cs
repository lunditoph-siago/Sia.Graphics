using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Graphics.Text;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public sealed class UiRenderer(UiPipeline pipeline)
{
    private const int MergeGapPrimitives = 16;
    private static readonly ulong _primitiveStride = (ulong)Marshal.SizeOf<UiPrimitive>();
    private readonly List<UiPrimitive> _uploadedPrimitives = [];
    private Entity _primitiveBuffer;
    private Entity _bindGroup;
    private ulong _primitiveBufferCapacity;
    private int _boundTextureVersion = -1;
    private long _uploadedVersion = -1;
    private Size? _uploadedViewport;

    public void Render(
        World world,
        WgpuReactiveRenderGraphPassContext context,
        RenderGraphTextureKey output,
        Size viewport,
        WGPULoadOp loadOp = WGPULoadOp.Load)
    {
        var primitiveCount = PrepareFrame(world, viewport);
        EncodeRenderPass(
            context.CommandEncoder,
            context.GetTextureView(output),
            primitiveCount,
            loadOp);
    }

    public uint PrepareFrame(World world, Size viewport)
    {
        var cache = world.AcquireAddon<UiRenderCache>();
        cache.Prepare();
        var queue = pipeline.Queue.GetWgpu<WGPUQueue>();

        if (_uploadedViewport != viewport) {
            Wgpu.WriteBuffer<float>(
                queue,
                pipeline.ViewUniformBuffer.GetWgpu<WGPUBuffer>(),
                0,
                UiOrthographicProjection.Build(viewport));
            _uploadedViewport = viewport;
        }

        if (_uploadedVersion != cache.PreparedVersion) {
            var primitives = CollectionsMarshal.AsSpan(cache.Primitives);
            var resized = EnsurePrimitiveBufferCapacity(world, (ulong)primitives.Length * _primitiveStride);
            UploadChangedPrimitives(queue, primitives, resized);
            _uploadedPrimitives.Clear();
            _uploadedPrimitives.AddRange(cache.Primitives);
            _uploadedVersion = cache.PreparedVersion;
        } else if (!_primitiveBuffer.IsValid) {
            EnsurePrimitiveBufferCapacity(world, _primitiveStride);
        }

        pipeline.UploadAtlases(world, world.AcquireAddon<FontAtlasSet>());
        EnsureBindGroup(world);
        return (uint)cache.Primitives.Count;
    }

    private bool EnsurePrimitiveBufferCapacity(World world, ulong requiredBytes)
    {
        requiredBytes = Math.Max(requiredBytes, _primitiveStride);
        if (_primitiveBufferCapacity >= requiredBytes)
            return false;

        var newCapacity = Math.Max(_primitiveBufferCapacity, _primitiveStride * 256);
        while (newCapacity < requiredBytes)
            newCapacity *= 2;

        if (_bindGroup.IsValid)
            _bindGroup.Destroy();
        if (_primitiveBuffer.IsValid)
            _primitiveBuffer.Destroy();

        _primitiveBuffer = world.CreateWgpuBuffer(pipeline.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
            Size = newCapacity,
            MappedAtCreation = 0
        });
        _primitiveBufferCapacity = newCapacity;
        return true;
    }

    private void EnsureBindGroup(World world)
    {
        if (_bindGroup.IsValid && _boundTextureVersion == pipeline.TextureVersion)
            return;
        if (_bindGroup.IsValid)
            _bindGroup.Destroy();
        _bindGroup = world.OwnWgpu(pipeline.CreateBindGroup(
            _primitiveBuffer.GetWgpu<WGPUBuffer>(),
            _primitiveBufferCapacity));
        _boundTextureVersion = pipeline.TextureVersion;
    }

    private void UploadChangedPrimitives(
        WgpuHandle<WGPUQueue> queue,
        ReadOnlySpan<UiPrimitive> primitives,
        bool resized)
    {
        if (primitives.IsEmpty)
            return;
        if (resized || primitives.Length != _uploadedPrimitives.Count) {
            Wgpu.WriteBuffer(
                queue,
                _primitiveBuffer.GetWgpu<WGPUBuffer>(),
                0,
                primitives);
            return;
        }

        var previous = CollectionsMarshal.AsSpan(_uploadedPrimitives);
        var cursor = 0;
        while (cursor < primitives.Length) {
            while (cursor < primitives.Length && Equal(primitives, previous, cursor))
                cursor++;
            if (cursor == primitives.Length)
                break;

            var first = cursor;
            var last = cursor;
            cursor++;
            while (cursor < primitives.Length) {
                if (!Equal(primitives, previous, cursor))
                    last = cursor;
                else if (cursor - last > MergeGapPrimitives)
                    break;
                cursor++;
            }

            Wgpu.WriteBuffer(
                queue,
                _primitiveBuffer.GetWgpu<WGPUBuffer>(),
                (ulong)first * _primitiveStride,
                primitives.Slice(first, last - first + 1));
        }
    }

    private static bool Equal(
        ReadOnlySpan<UiPrimitive> current,
        ReadOnlySpan<UiPrimitive> previous,
        int index) =>
        MemoryMarshal.AsBytes(current.Slice(index, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(previous.Slice(index, 1)));

    public void Encode(WgpuHandle<WGPURenderPassEncoder> renderPass, uint primitiveCount)
    {
        Wgpu.SetRenderPipeline(renderPass, pipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, _bindGroup.GetWgpu<WGPUBindGroup>());
        if (primitiveCount > 0)
            Wgpu.Draw(renderPass, 6, primitiveCount);
    }

    private unsafe void EncodeRenderPass(
        WgpuHandle<WGPUCommandEncoder> encoder,
        WgpuHandle<WGPUTextureView> target,
        uint primitiveCount,
        WGPULoadOp loadOp)
    {
        Span<WGPURenderPassColorAttachment> colorAttachments = stackalloc WGPURenderPassColorAttachment[1];
        colorAttachments[0] = WGPURenderPassColorAttachment.Default;
        colorAttachments[0].View = ToPointer(target);
        colorAttachments[0].LoadOp = loadOp;
        colorAttachments[0].StoreOp = WGPUStoreOp.Store;
        colorAttachments[0].ClearValue = new WGPUColor { R = 0, G = 0, B = 0, A = 0 };

        fixed (WGPURenderPassColorAttachment* colorAttachmentsPtr = colorAttachments) {
            var descriptor = WGPURenderPassDescriptor.Default;
            descriptor.ColorAttachmentCount = 1;
            descriptor.ColorAttachments = colorAttachmentsPtr;

            var renderPass = Wgpu.BeginRenderPass(encoder, in descriptor);
            try {
                Encode(renderPass, primitiveCount);
                Wgpu.EndRenderPass(renderPass);
            }
            finally {
                Wgpu.Release(ref renderPass);
            }
        }
    }

    private static unsafe WGPUTextureView* ToPointer(WgpuHandle<WGPUTextureView> handle) =>
        (WGPUTextureView*)handle.DangerousGetHandle();
}

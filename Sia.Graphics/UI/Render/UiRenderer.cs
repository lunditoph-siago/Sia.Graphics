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
    private readonly List<int> _dirtySlots = [];
    private Entity _primitiveBuffer;
    private Entity _paintOrderBuffer;
    private Entity _bindGroup;
    private ulong _primitiveBufferCapacity;
    private ulong _paintOrderBufferCapacity;
    private int _boundTextureVersion = -1;
    private long _uploadedVersion = -1;
    private Size? _uploadedViewport;

    public void Render(
        World world,
        WgpuReactiveRenderGraphPassContext context,
        RenderGraphTextureKey output,
        Size viewport,
        WGPULoadOp loadOp = WGPULoadOp.Load,
        bool outputCacheable = true)
    {
        var primitiveCount = PrepareFrame(world, viewport);
        var renderPass = context.GetOrBeginRenderPass(
            new WgpuReactiveRenderGraphColorAttachment(output, loadOp, Cacheable: outputCacheable));
        Encode(renderPass, primitiveCount);
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
            var paintOrder = CollectionsMarshal.AsSpan(cache.PaintOrder);
            cache.ConsumeChanges(_dirtySlots, out var paintOrderDirty);
            var primitivesResized = EnsurePrimitiveBufferCapacity(
                world,
                (ulong)primitives.Length * _primitiveStride);
            var paintOrderResized = EnsurePaintOrderBufferCapacity(
                world,
                (ulong)paintOrder.Length * sizeof(uint));
            UploadSlots(
                queue,
                _primitiveBuffer.GetWgpu<WGPUBuffer>(),
                primitives,
                _dirtySlots,
                primitivesResized,
                _primitiveStride,
                MergeGapPrimitives);
            if ((paintOrderResized || paintOrderDirty) && !paintOrder.IsEmpty) {
                Wgpu.WriteBuffer(queue, _paintOrderBuffer.GetWgpu<WGPUBuffer>(), 0, paintOrder);
            }
            _uploadedVersion = cache.PreparedVersion;
        } else if (!_primitiveBuffer.IsValid || !_paintOrderBuffer.IsValid) {
            EnsurePrimitiveBufferCapacity(world, _primitiveStride);
            EnsurePaintOrderBufferCapacity(world, sizeof(uint));
        }

        pipeline.UploadAtlases(world, world.AcquireAddon<FontAtlasSet>());
        EnsureBindGroup(world);
        return (uint)cache.PaintOrder.Count;
    }

    private bool EnsurePrimitiveBufferCapacity(World world, ulong requiredBytes) =>
        EnsureBufferCapacity(
            world,
            ref _primitiveBuffer,
            ref _primitiveBufferCapacity,
            requiredBytes,
            _primitiveStride);

    private bool EnsurePaintOrderBufferCapacity(World world, ulong requiredBytes) =>
        EnsureBufferCapacity(
            world,
            ref _paintOrderBuffer,
            ref _paintOrderBufferCapacity,
            requiredBytes,
            sizeof(uint));

    private bool EnsureBufferCapacity(
        World world, ref Entity buffer, ref ulong capacity, ulong requiredBytes, ulong stride)
    {
        requiredBytes = Math.Max(requiredBytes, stride);
        if (capacity >= requiredBytes)
            return false;

        var newCapacity = Math.Max(capacity, stride * 256);
        while (newCapacity < requiredBytes)
            newCapacity *= 2;

        if (_bindGroup.IsValid)
            _bindGroup.Destroy();
        if (buffer.IsValid)
            buffer.Destroy();

        buffer = world.CreateWgpuBuffer(pipeline.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
            Size = newCapacity,
            MappedAtCreation = 0
        });
        capacity = newCapacity;
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
            _primitiveBufferCapacity,
            _paintOrderBuffer.GetWgpu<WGPUBuffer>(),
            _paintOrderBufferCapacity));
        _boundTextureVersion = pipeline.TextureVersion;
    }

    private static void UploadSlots<T>(
        WgpuHandle<WGPUQueue> queue,
        WgpuHandle<WGPUBuffer> buffer,
        ReadOnlySpan<T> current,
        List<int> dirtySlots,
        bool resized,
        ulong stride,
        int mergeGap)
        where T : unmanaged
    {
        if (current.IsEmpty) {
            return;
        }
        if (resized) {
            Wgpu.WriteBuffer(queue, buffer, 0, current);
            return;
        }
        if (dirtySlots.Count == 0) {
            return;
        }

        dirtySlots.Sort();
        var first = dirtySlots[0];
        var last = first;
        for (var index = 1; index < dirtySlots.Count; index++) {
            var slot = dirtySlots[index];
            if (slot <= last + mergeGap + 1) {
                last = Math.Max(last, slot);
                continue;
            }
            UploadRange(queue, buffer, current, first, last, stride);
            first = slot;
            last = slot;
        }
        UploadRange(queue, buffer, current, first, last, stride);
    }

    private static void UploadRange<T>(
        WgpuHandle<WGPUQueue> queue,
        WgpuHandle<WGPUBuffer> buffer,
        ReadOnlySpan<T> current,
        int first,
        int last,
        ulong stride)
        where T : unmanaged =>
        Wgpu.WriteBuffer(queue, buffer, (ulong)first * stride, current.Slice(first, last - first + 1));

    public void Encode(WgpuHandle<WGPURenderPassEncoder> renderPass, uint primitiveCount)
    {
        Wgpu.SetRenderPipeline(renderPass, pipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, _bindGroup.GetWgpu<WGPUBindGroup>());
        if (primitiveCount > 0)
            Wgpu.Draw(renderPass, 6, primitiveCount);
    }

}

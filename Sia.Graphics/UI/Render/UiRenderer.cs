using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Graphics.Text;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public sealed class UiRenderer(UiPipeline pipeline)
{
    private const int k_MergeGapPrimitives = 16;
    private static readonly ulong s_PrimitiveStride = (ulong)Marshal.SizeOf<UiPrimitive>();
    private readonly List<int> _dirtySlots = [];
    private readonly List<UiPrimitive> _orderedPrimitives = [];
    private Entity _primitiveBuffer;
    private Entity _paintOrderBuffer;
    private Entity _compatibilityVertexBuffer;
    private Entity _bindGroup;
    private ulong _primitiveBufferCapacity;
    private ulong _paintOrderBufferCapacity;
    private ulong _compatibilityVertexBufferCapacity;
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
            if (pipeline.UsesVertexStorage)
                UploadStorageData(world, queue, cache);
            else
                UploadCompatibilityData(world, queue, cache);
            _uploadedVersion = cache.PreparedVersion;
        }
        else if (pipeline.UsesVertexStorage
            && (!_primitiveBuffer.IsValid || !_paintOrderBuffer.IsValid)) {
            EnsurePrimitiveBufferCapacity(world, s_PrimitiveStride);
            EnsurePaintOrderBufferCapacity(world, sizeof(uint));
        }
        else if (!pipeline.UsesVertexStorage && !_compatibilityVertexBuffer.IsValid) {
            EnsureCompatibilityVertexBufferCapacity(world, s_PrimitiveStride);
        }

        pipeline.UploadAtlases(world, world.AcquireAddon<FontAtlasSet>());
        EnsureBindGroup(world);
        return (uint)cache.PaintOrder.Count;
    }

    private void UploadStorageData(
        World world,
        WgpuHandle<WGPUQueue> queue,
        UiRenderCache cache)
    {
        var primitives = CollectionsMarshal.AsSpan(cache.Primitives);
        var paintOrder = CollectionsMarshal.AsSpan(cache.PaintOrder);
        cache.ConsumeChanges(_dirtySlots, out var paintOrderDirty);
        var primitivesResized = EnsurePrimitiveBufferCapacity(
            world,
            (ulong)primitives.Length * s_PrimitiveStride);
        var paintOrderResized = EnsurePaintOrderBufferCapacity(
            world,
            (ulong)paintOrder.Length * sizeof(uint));
        UploadSlots(
            queue,
            _primitiveBuffer.GetWgpu<WGPUBuffer>(),
            primitives,
            _dirtySlots,
            primitivesResized,
            s_PrimitiveStride,
            k_MergeGapPrimitives);
        if ((paintOrderResized || paintOrderDirty) && !paintOrder.IsEmpty)
            Wgpu.WriteBuffer(queue, _paintOrderBuffer.GetWgpu<WGPUBuffer>(), 0, paintOrder);
    }

    private void UploadCompatibilityData(
        World world,
        WgpuHandle<WGPUQueue> queue,
        UiRenderCache cache)
    {
        cache.ConsumeChanges(_dirtySlots, out _);
        _orderedPrimitives.Clear();
        _orderedPrimitives.EnsureCapacity(cache.PaintOrder.Count);
        foreach (var slot in cache.PaintOrder)
            _orderedPrimitives.Add(cache.Primitives[(int)slot]);

        EnsureCompatibilityVertexBufferCapacity(
            world,
            (ulong)_orderedPrimitives.Count * s_PrimitiveStride);
        if (_orderedPrimitives.Count != 0) {
            Wgpu.WriteBuffer(
                queue,
                _compatibilityVertexBuffer.GetWgpu<WGPUBuffer>(),
                0,
                CollectionsMarshal.AsSpan(_orderedPrimitives));
        }
    }

    private bool EnsurePrimitiveBufferCapacity(World world, ulong requiredBytes) =>
        EnsureBufferCapacity(
            world,
            ref _primitiveBuffer,
            ref _primitiveBufferCapacity,
            requiredBytes,
            s_PrimitiveStride,
            WGPUBufferUsage.Storage,
            invalidateBindGroup: true);

    private bool EnsurePaintOrderBufferCapacity(World world, ulong requiredBytes) =>
        EnsureBufferCapacity(
            world,
            ref _paintOrderBuffer,
            ref _paintOrderBufferCapacity,
            requiredBytes,
            sizeof(uint),
            WGPUBufferUsage.Storage,
            invalidateBindGroup: true);

    private bool EnsureCompatibilityVertexBufferCapacity(World world, ulong requiredBytes) =>
        EnsureBufferCapacity(
            world,
            ref _compatibilityVertexBuffer,
            ref _compatibilityVertexBufferCapacity,
            requiredBytes,
            s_PrimitiveStride,
            WGPUBufferUsage.Vertex,
            invalidateBindGroup: false);

    private bool EnsureBufferCapacity(
        World world,
        ref Entity buffer,
        ref ulong capacity,
        ulong requiredBytes,
        ulong stride,
        WGPUBufferUsage usage,
        bool invalidateBindGroup)
    {
        requiredBytes = System.Math.Max(requiredBytes, stride);
        if (capacity >= requiredBytes)
            return false;

        var newCapacity = System.Math.Max(capacity, stride * 256);
        while (newCapacity < requiredBytes)
            newCapacity *= 2;

        if (invalidateBindGroup && _bindGroup.IsValid)
            _bindGroup.Destroy();
        if (buffer.IsValid)
            buffer.Destroy();

        buffer = world.CreateWgpuBuffer(pipeline.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = usage | WGPUBufferUsage.CopyDst,
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
        _bindGroup = world.OwnWgpu(pipeline.UsesVertexStorage
            ? pipeline.CreateBindGroup(
                _primitiveBuffer.GetWgpu<WGPUBuffer>(),
                _primitiveBufferCapacity,
                _paintOrderBuffer.GetWgpu<WGPUBuffer>(),
                _paintOrderBufferCapacity)
            : pipeline.CreateCompatibilityBindGroup());
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
        if (current.IsEmpty)
            return;
        if (resized) {
            Wgpu.WriteBuffer(queue, buffer, 0, current);
            return;
        }
        if (dirtySlots.Count == 0)
            return;

        dirtySlots.Sort();
        var first = dirtySlots[0];
        var last = first;
        for (var index = 1; index < dirtySlots.Count; index++) {
            var slot = dirtySlots[index];
            if (slot <= last + mergeGap + 1) {
                last = System.Math.Max(last, slot);
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
        if (!pipeline.UsesVertexStorage) {
            Wgpu.SetVertexBuffer(
                renderPass,
                0,
                _compatibilityVertexBuffer.GetWgpu<WGPUBuffer>());
        }
        if (primitiveCount > 0)
            Wgpu.Draw(renderPass, 6, primitiveCount);
    }
}

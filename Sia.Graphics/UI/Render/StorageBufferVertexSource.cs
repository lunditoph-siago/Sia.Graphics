using System.Runtime.InteropServices;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

internal sealed unsafe class StorageBufferVertexSource : IUiVertexSource
{
    private const int k_MergeGapPrimitives = 16;

    private readonly List<int> _dirtySlots = [];
    private Entity _primitiveBuffer;
    private Entity _paintOrderBuffer;
    private ulong _primitiveBufferCapacity;
    private ulong _paintOrderBufferCapacity;

    public Entity LoadVertexShaderModule(
        World world, WgpuHandle<WGPUDevice> device, Entity fragmentShaderModule) =>
        fragmentShaderModule;

    public int WriteBindGroupLayoutEntries(Span<WGPUBindGroupLayoutEntry> entries)
    {
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = 1;
        entries[0].Visibility = WGPUShaderStage.Vertex;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;
        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = 2;
        entries[1].Visibility = WGPUShaderStage.Vertex;
        entries[1].Buffer = WGPUBufferBindingLayout.Default;
        entries[1].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;
        return 2;
    }

    public int WriteVertexAttributes(Span<WGPUVertexAttribute> attributes) => 0;

    public int WriteBindGroupEntries(Span<WGPUBindGroupEntry> entries)
    {
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = 1,
            Buffer = (WGPUBuffer*)_primitiveBuffer.GetWgpu<WGPUBuffer>().DangerousGetHandle(),
            Size = _primitiveBufferCapacity
        };
        entries[1] = WGPUBindGroupEntry.Default with {
            Binding = 2,
            Buffer = (WGPUBuffer*)_paintOrderBuffer.GetWgpu<WGPUBuffer>().DangerousGetHandle(),
            Size = _paintOrderBufferCapacity
        };
        return 2;
    }

    public bool UploadFrame(World world, Entity device, WgpuHandle<WGPUQueue> queue, UiRenderCache cache)
    {
        var primitives = CollectionsMarshal.AsSpan(cache.Primitives);
        var paintOrder = CollectionsMarshal.AsSpan(cache.PaintOrder);
        cache.ConsumeChanges(_dirtySlots, out var paintOrderDirty);

        var primitivesResized = UiGpuBuffer.EnsureCapacity(
            world, device, ref _primitiveBuffer, ref _primitiveBufferCapacity,
            (ulong)primitives.Length * UiPrimitive.Stride, UiPrimitive.Stride, WGPUBufferUsage.Storage);
        var paintOrderResized = UiGpuBuffer.EnsureCapacity(
            world, device, ref _paintOrderBuffer, ref _paintOrderBufferCapacity,
            (ulong)paintOrder.Length * sizeof(uint), sizeof(uint), WGPUBufferUsage.Storage);

        UploadSlots(
            queue,
            _primitiveBuffer.GetWgpu<WGPUBuffer>(),
            primitives,
            _dirtySlots,
            primitivesResized,
            UiPrimitive.Stride,
            k_MergeGapPrimitives);
        if ((paintOrderResized || paintOrderDirty) && !paintOrder.IsEmpty)
            Wgpu.WriteBuffer(queue, _paintOrderBuffer.GetWgpu<WGPUBuffer>(), 0, paintOrder);

        return primitivesResized || paintOrderResized;
    }

    public bool EnsureBuffers(World world, Entity device)
    {
        if (_primitiveBuffer.IsValid && _paintOrderBuffer.IsValid)
            return false;

        var primitivesResized = UiGpuBuffer.EnsureCapacity(
            world, device, ref _primitiveBuffer, ref _primitiveBufferCapacity,
            UiPrimitive.Stride, UiPrimitive.Stride, WGPUBufferUsage.Storage);
        var paintOrderResized = UiGpuBuffer.EnsureCapacity(
            world, device, ref _paintOrderBuffer, ref _paintOrderBufferCapacity,
            sizeof(uint), sizeof(uint), WGPUBufferUsage.Storage);
        return primitivesResized || paintOrderResized;
    }

    public void BindForDraw(WgpuHandle<WGPURenderPassEncoder> renderPass) { }

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
}

using System.Runtime.InteropServices;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

internal sealed unsafe class VertexBufferVertexSource : IUiVertexSource
{
    private readonly List<int> _dirtySlots = [];
    private readonly List<UiPrimitive> _orderedPrimitives = [];
    private Entity _vertexBuffer;
    private ulong _vertexBufferCapacity;

    public Entity LoadVertexShaderModule(
        World world, WgpuHandle<WGPUDevice> device, Entity fragmentShaderModule) =>
        world.OwnWgpu(Wgpu.CreateWgslShaderModule(
            device, UiShaderSource.LoadVertexBuffer(), "ui_node_vertex_buffer"));

    public int WriteBindGroupLayoutEntries(Span<WGPUBindGroupLayoutEntry> entries) => 0;

    public int WriteVertexAttributes(Span<WGPUVertexAttribute> attributes)
    {
        attributes[0] = Attribute(WGPUVertexFormat.Float32x4, nameof(UiPrimitive.TransformM11), 0);
        attributes[1] = Attribute(WGPUVertexFormat.Float32x4, nameof(UiPrimitive.TranslateX), 1);
        attributes[2] = Attribute(WGPUVertexFormat.Float32x4, nameof(UiPrimitive.SizeX), 2);
        attributes[3] = Attribute(WGPUVertexFormat.Uint32x2, nameof(UiPrimitive.RadiusTop), 3);
        attributes[4] = Attribute(WGPUVertexFormat.Uint32x2, nameof(UiPrimitive.BorderLeftTop), 4);
        attributes[5] = Attribute(WGPUVertexFormat.Float32x4, nameof(UiPrimitive.ClipLeft), 5);
        attributes[6] = Attribute(WGPUVertexFormat.Uint32, nameof(UiPrimitive.PackedColor), 6);
        return 7;
    }

    public int WriteBindGroupEntries(Span<WGPUBindGroupEntry> entries) => 0;

    public bool UploadFrame(World world, Entity device, WgpuHandle<WGPUQueue> queue, UiRenderCache cache)
    {
        cache.ConsumeChanges(_dirtySlots, out _);
        _orderedPrimitives.Clear();
        _orderedPrimitives.EnsureCapacity(cache.PaintOrder.Count);
        foreach (var slot in cache.PaintOrder)
            _orderedPrimitives.Add(cache.Primitives[(int)slot]);

        UiGpuBuffer.EnsureCapacity(
            world, device, ref _vertexBuffer, ref _vertexBufferCapacity,
            (ulong)_orderedPrimitives.Count * UiPrimitive.Stride, UiPrimitive.Stride, WGPUBufferUsage.Vertex);
        if (_orderedPrimitives.Count != 0) {
            Wgpu.WriteBuffer(
                queue,
                _vertexBuffer.GetWgpu<WGPUBuffer>(),
                0,
                CollectionsMarshal.AsSpan(_orderedPrimitives));
        }

        return false;
    }

    public bool EnsureBuffers(World world, Entity device)
    {
        if (_vertexBuffer.IsValid)
            return false;
        UiGpuBuffer.EnsureCapacity(
            world, device, ref _vertexBuffer, ref _vertexBufferCapacity,
            UiPrimitive.Stride, UiPrimitive.Stride, WGPUBufferUsage.Vertex);
        return false;
    }

    public void BindForDraw(WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Wgpu.SetVertexBuffer(renderPass, 0, _vertexBuffer.GetWgpu<WGPUBuffer>());

    private static WGPUVertexAttribute Attribute(WGPUVertexFormat format, string primitiveFieldName, uint location) =>
        WGPUVertexAttribute.Default with {
            Format = format,
            Offset = (ulong)Marshal.OffsetOf<UiPrimitive>(primitiveFieldName),
            ShaderLocation = location
        };
}

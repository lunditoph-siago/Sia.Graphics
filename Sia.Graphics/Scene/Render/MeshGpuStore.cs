using Sia;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed class MeshGpuStore : IAddon
{
    private readonly Dictionary<MeshHandle, GpuMesh> _meshes = [];

    public GpuMesh GetOrUpload(in GpuFrame frame, MeshRegistry registry, MeshHandle handle)
    {
        if (_meshes.TryGetValue(handle, out var mesh)) {
            return mesh;
        }

        var data = registry.Get(handle);
        var vertexBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst,
            Size = (ulong)data.Vertices.Length * MeshVertex.Stride,
            MappedAtCreation = 0
        });
        Wgpu.WriteBuffer<MeshVertex>(
            frame.Queue.GetWgpu<WGPUQueue>(), vertexBuffer.GetWgpu<WGPUBuffer>(), 0, data.Vertices);

        var indexBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst,
            Size = (ulong)data.Indices.Length * sizeof(uint),
            MappedAtCreation = 0
        });
        Wgpu.WriteBuffer<uint>(
            frame.Queue.GetWgpu<WGPUQueue>(), indexBuffer.GetWgpu<WGPUBuffer>(), 0, data.Indices);

        mesh = new GpuMesh(vertexBuffer, indexBuffer, (uint)data.Indices.Length);
        _meshes[handle] = mesh;
        return mesh;
    }
}

public readonly record struct GpuMesh(Entity VertexBuffer, Entity IndexBuffer, uint IndexCount);

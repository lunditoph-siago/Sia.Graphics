using Sia;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed class InstanceGpuStore
{
    private Entity _buffer;
    private ulong _capacity;

    public Entity Buffer => _buffer;

    public ulong Capacity => _capacity;

    public bool IsValid => _buffer.IsValid;

    public bool Upload(in GpuFrame frame, ReadOnlySpan<RenderInstance> instances)
    {
        var requiredBytes = (ulong)instances.Length * RenderInstance.Stride;
        var resized = EnsureCapacity(frame, requiredBytes);
        if (!instances.IsEmpty) {
            Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), _buffer.GetWgpu<WGPUBuffer>(), 0, instances);
        }
        return resized;
    }

    private bool EnsureCapacity(in GpuFrame frame, ulong requiredBytes)
    {
        requiredBytes = System.Math.Max(requiredBytes, RenderInstance.Stride);
        if (_capacity >= requiredBytes) {
            return false;
        }

        var newCapacity = System.Math.Max(_capacity, (ulong)RenderInstance.Stride * 256);
        while (newCapacity < requiredBytes) {
            newCapacity *= 2;
        }

        if (_buffer.IsValid) {
            _buffer.Destroy();
        }
        _buffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
            Size = newCapacity,
            MappedAtCreation = 0
        });
        _capacity = newCapacity;
        return true;
    }
}

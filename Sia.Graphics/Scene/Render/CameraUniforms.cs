using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct CameraUniformData(float4x4 ViewProj, float4 WorldPosition)
{
    public const int Stride = 80;
}

public sealed class CameraUniforms
{
    private Entity _buffer;

    public Entity Buffer => _buffer;

    public void Update(in GpuFrame frame, in CameraMatrices matrices)
    {
        if (!_buffer.IsValid) {
            _buffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = CameraUniformData.Stride,
                MappedAtCreation = 0
            });
        }

        var data = new CameraUniformData(matrices.ViewProj, new float4(matrices.WorldPosition, 1.0f));
        Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), _buffer.GetWgpu<WGPUBuffer>(), 0, [data]);
    }
}

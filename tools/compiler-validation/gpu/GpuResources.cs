using Sia.WebGPU;

namespace SiaGpuDiagnostics;

// Task-owned fixture resources; no renderer state is involved in translator execution checks.
internal readonly record struct GpuResource(nint Handle)
{
    internal WgpuHandle<T> GetWgpu<T>() where T : unmanaged => new(Handle);
}
internal sealed class GpuResources(WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue) : IDisposable
{
    private readonly List<Action> release = [];
    internal WgpuHandle<WGPUDevice> Device => device;
    internal GpuResource Own<T>(WgpuHandle<T> handle) where T : unmanaged
    {
        release.Add(() => { var owned = handle; Wgpu.Release(ref owned); });
        return new(handle.DangerousGetHandle());
    }
    internal unsafe GpuResource Buffer(ulong size, WGPUBufferUsage usage)
    {
        var descriptor = WGPUBufferDescriptor.Default; descriptor.Size = size; descriptor.Usage = usage;
        return Own(Wgpu.CreateBuffer(device, descriptor));
    }
    internal unsafe GpuResource Upload<T>(ReadOnlySpan<T> data, WGPUBufferUsage usage) where T : unmanaged
    {
        var buffer = Buffer((ulong)data.Length * (ulong)sizeof(T), usage | WGPUBufferUsage.CopyDst);
        Wgpu.WriteBuffer(queue, buffer.GetWgpu<WGPUBuffer>(), 0, data); return buffer;
    }
    public void Dispose() { for (int i = release.Count - 1; i >= 0; i--) release[i](); release.Clear(); }
}
internal static unsafe class GpuBinding
{
    internal static WGPUBindGroupLayoutEntry Buffer(uint binding, WGPUBufferBindingType type, WGPUShaderStage visibility)
    {
        var entry = WGPUBindGroupLayoutEntry.Default; entry.Binding = binding; entry.Visibility = visibility;
        entry.Buffer = WGPUBufferBindingLayout.Default; entry.Buffer.Type = type; return entry;
    }
    internal static WGPUBindGroupEntry Buffer(uint binding, GpuResource buffer)
    {
        var entry = WGPUBindGroupEntry.Default; entry.Binding = binding; entry.Buffer = (WGPUBuffer*)buffer.Handle;
        entry.Size = Wgpu.GetBufferSize(buffer.GetWgpu<WGPUBuffer>()); return entry;
    }
    internal static GpuResource Layout(GpuResources gpu, ReadOnlySpan<WGPUBindGroupLayoutEntry> entries)
    {
        fixed (WGPUBindGroupLayoutEntry* pointer = entries)
        {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default; descriptor.Entries = pointer; descriptor.EntryCount = (nuint)entries.Length;
            return gpu.Own(Wgpu.CreateBindGroupLayout(gpu.Device, descriptor));
        }
    }
    internal static GpuResource Group(GpuResources gpu, GpuResource layout, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        fixed (WGPUBindGroupEntry* pointer = entries)
        {
            var descriptor = WGPUBindGroupDescriptor.Default; descriptor.Layout = (WGPUBindGroupLayout*)layout.Handle;
            descriptor.Entries = pointer; descriptor.EntryCount = (nuint)entries.Length;
            return gpu.Own(Wgpu.CreateBindGroup(gpu.Device, descriptor));
        }
    }
    internal static GpuResource PipelineLayout(GpuResources gpu, GpuResource layout)
    {
        var handle = (WGPUBindGroupLayout*)layout.Handle;
        var descriptor = WGPUPipelineLayoutDescriptor.Default; descriptor.BindGroupLayouts = &handle; descriptor.BindGroupLayoutCount = 1;
        return gpu.Own(Wgpu.CreatePipelineLayout(gpu.Device, descriptor));
    }
}

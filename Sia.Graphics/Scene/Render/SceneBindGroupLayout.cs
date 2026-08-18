using Sia.WebGPU;

namespace Sia.Graphics.Scene;

internal static unsafe class SceneBindGroupLayout
{
    public const uint CameraBinding = 0;
    public const uint InstanceBinding = 1;
    private const int EntryCount = 2;

    public static WgpuHandle<WGPUBindGroupLayout> Create(
        WgpuHandle<WGPUDevice> device,
        WGPUShaderStage instanceVisibility)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[EntryCount];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = CameraBinding;
        entries[0].Visibility = WGPUShaderStage.Vertex | WGPUShaderStage.Fragment;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;

        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = InstanceBinding;
        entries[1].Visibility = instanceVisibility;
        entries[1].Buffer = WGPUBufferBindingLayout.Default;
        entries[1].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = EntryCount;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroupLayout(device, in descriptor);
        }
    }

    public static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> layout)
    {
        var layoutPtr = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 1;
        descriptor.BindGroupLayouts = &layoutPtr;
        return Wgpu.CreatePipelineLayout(device, in descriptor);
    }

    public static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> layout0,
        WgpuHandle<WGPUBindGroupLayout> layout1)
    {
        var layouts = stackalloc WGPUBindGroupLayout*[2];
        layouts[0] = (WGPUBindGroupLayout*)layout0.DangerousGetHandle();
        layouts[1] = (WGPUBindGroupLayout*)layout1.DangerousGetHandle();
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 2;
        descriptor.BindGroupLayouts = layouts;
        return Wgpu.CreatePipelineLayout(device, in descriptor);
    }

    public static WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> layout,
        WgpuHandle<WGPUBuffer> cameraBuffer,
        WgpuHandle<WGPUBuffer> instanceBuffer,
        ulong instanceBufferSize)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[EntryCount];
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = CameraBinding,
            Buffer = (WGPUBuffer*)cameraBuffer.DangerousGetHandle(),
            Size = CameraUniformData.Stride
        };
        entries[1] = WGPUBindGroupEntry.Default with {
            Binding = InstanceBinding,
            Buffer = (WGPUBuffer*)instanceBuffer.DangerousGetHandle(),
            Size = instanceBufferSize
        };

        fixed (WGPUBindGroupEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
            descriptor.EntryCount = EntryCount;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroup(device, in descriptor);
        }
    }
}

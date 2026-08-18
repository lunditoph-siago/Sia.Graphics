using Sia.WebGPU;

namespace Sia.Graphics.Scene;

internal static unsafe class SceneIblBindGroupLayout
{
    public const uint ShBinding = 0;
    public const uint PrefilteredBinding = 1;
    public const uint PrefilteredSamplerBinding = 2;
    public const uint BrdfLutBinding = 3;
    public const uint BrdfLutSamplerBinding = 4;
    private const int EntryCount = 5;

    public static WgpuHandle<WGPUBindGroupLayout> Create(WgpuHandle<WGPUDevice> device)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[EntryCount];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = ShBinding;
        entries[0].Visibility = WGPUShaderStage.Fragment;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;

        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = PrefilteredBinding;
        entries[1].Visibility = WGPUShaderStage.Fragment;
        entries[1].Texture = WGPUTextureBindingLayout.Default;
        entries[1].Texture.SampleType = WGPUTextureSampleType.Float;
        entries[1].Texture.ViewDimension = WGPUTextureViewDimension.Cube;
        entries[1].Texture.Multisampled = 0;

        entries[2] = WGPUBindGroupLayoutEntry.Default;
        entries[2].Binding = PrefilteredSamplerBinding;
        entries[2].Visibility = WGPUShaderStage.Fragment;
        entries[2].Sampler = WGPUSamplerBindingLayout.Default;
        entries[2].Sampler.Type = WGPUSamplerBindingType.Filtering;

        entries[3] = WGPUBindGroupLayoutEntry.Default;
        entries[3].Binding = BrdfLutBinding;
        entries[3].Visibility = WGPUShaderStage.Fragment;
        entries[3].Texture = WGPUTextureBindingLayout.Default;
        entries[3].Texture.SampleType = WGPUTextureSampleType.Float;
        entries[3].Texture.ViewDimension = WGPUTextureViewDimension._2D;
        entries[3].Texture.Multisampled = 0;

        entries[4] = WGPUBindGroupLayoutEntry.Default;
        entries[4].Binding = BrdfLutSamplerBinding;
        entries[4].Visibility = WGPUShaderStage.Fragment;
        entries[4].Sampler = WGPUSamplerBindingLayout.Default;
        entries[4].Sampler.Type = WGPUSamplerBindingType.Filtering;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = EntryCount;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroupLayout(device, in descriptor);
        }
    }

    public static WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> layout,
        WgpuHandle<WGPUBuffer> shBuffer,
        WgpuHandle<WGPUTextureView> prefiltered,
        WgpuHandle<WGPUSampler> prefilteredSampler,
        WgpuHandle<WGPUTextureView> brdfLut,
        WgpuHandle<WGPUSampler> brdfLutSampler)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[EntryCount];
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = ShBinding,
            Buffer = (WGPUBuffer*)shBuffer.DangerousGetHandle(),
            Size = IblShGpu.Stride
        };
        entries[1] = WGPUBindGroupEntry.Default with {
            Binding = PrefilteredBinding,
            TextureView = (WGPUTextureView*)prefiltered.DangerousGetHandle()
        };
        entries[2] = WGPUBindGroupEntry.Default with {
            Binding = PrefilteredSamplerBinding,
            Sampler = (WGPUSampler*)prefilteredSampler.DangerousGetHandle()
        };
        entries[3] = WGPUBindGroupEntry.Default with {
            Binding = BrdfLutBinding,
            TextureView = (WGPUTextureView*)brdfLut.DangerousGetHandle()
        };
        entries[4] = WGPUBindGroupEntry.Default with {
            Binding = BrdfLutSamplerBinding,
            Sampler = (WGPUSampler*)brdfLutSampler.DangerousGetHandle()
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

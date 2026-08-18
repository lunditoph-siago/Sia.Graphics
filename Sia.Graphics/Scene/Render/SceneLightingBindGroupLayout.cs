using Sia.WebGPU;

namespace Sia.Graphics.Scene;

internal static unsafe class SceneLightingBindGroupLayout
{
    public const uint ClusterConfigBinding = 0;
    public const uint ClusteredLightsBinding = 1;
    public const uint LightGridBinding = 2;
    public const uint LightIndexListBinding = 3;
    public const uint DirectionalLightsBinding = 4;
    public const uint ShadowAtlasBinding = 5;
    public const uint ShadowSamplerBinding = 6;
    public const uint ShadowLayersBinding = 7;
    public const uint ShadowConfigBinding = 8;
    private const int EntryCount = 9;

    public static WgpuHandle<WGPUBindGroupLayout> Create(WgpuHandle<WGPUDevice> device)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[EntryCount];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = ClusterConfigBinding;
        entries[0].Visibility = WGPUShaderStage.Fragment;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;

        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = ClusteredLightsBinding;
        entries[1].Visibility = WGPUShaderStage.Fragment;
        entries[1].Buffer = WGPUBufferBindingLayout.Default;
        entries[1].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;

        entries[2] = WGPUBindGroupLayoutEntry.Default;
        entries[2].Binding = LightGridBinding;
        entries[2].Visibility = WGPUShaderStage.Fragment;
        entries[2].Buffer = WGPUBufferBindingLayout.Default;
        entries[2].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;

        entries[3] = WGPUBindGroupLayoutEntry.Default;
        entries[3].Binding = LightIndexListBinding;
        entries[3].Visibility = WGPUShaderStage.Fragment;
        entries[3].Buffer = WGPUBufferBindingLayout.Default;
        entries[3].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;

        entries[4] = WGPUBindGroupLayoutEntry.Default;
        entries[4].Binding = DirectionalLightsBinding;
        entries[4].Visibility = WGPUShaderStage.Fragment;
        entries[4].Buffer = WGPUBufferBindingLayout.Default;
        entries[4].Buffer.Type = WGPUBufferBindingType.Uniform;

        entries[5] = WGPUBindGroupLayoutEntry.Default;
        entries[5].Binding = ShadowAtlasBinding;
        entries[5].Visibility = WGPUShaderStage.Fragment;
        entries[5].Texture = WGPUTextureBindingLayout.Default;
        entries[5].Texture.SampleType = WGPUTextureSampleType.Depth;
        entries[5].Texture.ViewDimension = WGPUTextureViewDimension._2DArray;
        entries[5].Texture.Multisampled = 0;

        entries[6] = WGPUBindGroupLayoutEntry.Default;
        entries[6].Binding = ShadowSamplerBinding;
        entries[6].Visibility = WGPUShaderStage.Fragment;
        entries[6].Sampler = WGPUSamplerBindingLayout.Default;
        entries[6].Sampler.Type = WGPUSamplerBindingType.Comparison;

        entries[7] = WGPUBindGroupLayoutEntry.Default;
        entries[7].Binding = ShadowLayersBinding;
        entries[7].Visibility = WGPUShaderStage.Fragment;
        entries[7].Buffer = WGPUBufferBindingLayout.Default;
        entries[7].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;

        entries[8] = WGPUBindGroupLayoutEntry.Default;
        entries[8].Binding = ShadowConfigBinding;
        entries[8].Visibility = WGPUShaderStage.Fragment;
        entries[8].Buffer = WGPUBufferBindingLayout.Default;
        entries[8].Buffer.Type = WGPUBufferBindingType.Uniform;

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
        WgpuHandle<WGPUBuffer> clusterConfig,
        WgpuHandle<WGPUBuffer> clusteredLights,
        ulong clusteredLightsSize,
        WgpuHandle<WGPUBuffer> lightGrid,
        ulong lightGridSize,
        WgpuHandle<WGPUBuffer> lightIndexList,
        ulong lightIndexListSize,
        WgpuHandle<WGPUBuffer> directionalLights,
        WgpuHandle<WGPUTextureView> shadowAtlas,
        WgpuHandle<WGPUSampler> shadowSampler,
        WgpuHandle<WGPUBuffer> shadowLayers,
        ulong shadowLayersSize,
        WgpuHandle<WGPUBuffer> shadowConfig)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[EntryCount];
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = ClusterConfigBinding,
            Buffer = (WGPUBuffer*)clusterConfig.DangerousGetHandle(),
            Size = ClusterConfigGpu.Stride
        };
        entries[1] = WGPUBindGroupEntry.Default with {
            Binding = ClusteredLightsBinding,
            Buffer = (WGPUBuffer*)clusteredLights.DangerousGetHandle(),
            Size = clusteredLightsSize
        };
        entries[2] = WGPUBindGroupEntry.Default with {
            Binding = LightGridBinding,
            Buffer = (WGPUBuffer*)lightGrid.DangerousGetHandle(),
            Size = lightGridSize
        };
        entries[3] = WGPUBindGroupEntry.Default with {
            Binding = LightIndexListBinding,
            Buffer = (WGPUBuffer*)lightIndexList.DangerousGetHandle(),
            Size = lightIndexListSize
        };
        entries[4] = WGPUBindGroupEntry.Default with {
            Binding = DirectionalLightsBinding,
            Buffer = (WGPUBuffer*)directionalLights.DangerousGetHandle(),
            Size = DirectionalLightBufferGpu.Stride
        };
        entries[5] = WGPUBindGroupEntry.Default with {
            Binding = ShadowAtlasBinding,
            TextureView = (WGPUTextureView*)shadowAtlas.DangerousGetHandle()
        };
        entries[6] = WGPUBindGroupEntry.Default with {
            Binding = ShadowSamplerBinding,
            Sampler = (WGPUSampler*)shadowSampler.DangerousGetHandle()
        };
        entries[7] = WGPUBindGroupEntry.Default with {
            Binding = ShadowLayersBinding,
            Buffer = (WGPUBuffer*)shadowLayers.DangerousGetHandle(),
            Size = shadowLayersSize
        };
        entries[8] = WGPUBindGroupEntry.Default with {
            Binding = ShadowConfigBinding,
            Buffer = (WGPUBuffer*)shadowConfig.DangerousGetHandle(),
            Size = ShadowConfigGpu.Stride
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

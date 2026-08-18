using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct IblPrefilterParamsGpu(float4 Params, float4 SunDir, float4 SunColor)
{
    public const int Stride = 48;
}

internal static unsafe class IblPrefilterBindGroupLayout
{
    public const uint ParamsBinding = 0;
    private const int EntryCount = 1;

    public static WgpuHandle<WGPUBindGroupLayout> Create(WgpuHandle<WGPUDevice> device)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[EntryCount];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = ParamsBinding;
        entries[0].Visibility = WGPUShaderStage.Fragment;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = EntryCount;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroupLayout(device, in descriptor);
        }
    }

    public static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUBindGroupLayout> layout)
    {
        var layoutPtr = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 1;
        descriptor.BindGroupLayouts = &layoutPtr;
        return Wgpu.CreatePipelineLayout(device, in descriptor);
    }

    public static WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUBindGroupLayout> layout, WgpuHandle<WGPUBuffer> paramsBuffer)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[EntryCount];
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = ParamsBinding,
            Buffer = (WGPUBuffer*)paramsBuffer.DangerousGetHandle(),
            Size = IblPrefilterParamsGpu.Stride
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

public sealed unsafe class IblPrecomputePipelines
{
    internal Entity PrefilterPipeline { get; }
    internal Entity PrefilterBindGroupLayout { get; }
    internal Entity BrdfLutPipeline { get; }

    private IblPrecomputePipelines(Entity prefilterPipeline, Entity prefilterBindGroupLayout, Entity brdfLutPipeline)
    {
        PrefilterPipeline = prefilterPipeline;
        PrefilterBindGroupLayout = prefilterBindGroupLayout;
        BrdfLutPipeline = brdfLutPipeline;
    }

    public static IblPrecomputePipelines Create(World world, Entity device)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();

        var prefilterShader = world.OwnWgpu(Wgpu.CreateWgslShaderModule(
            deviceHandle, SceneShaderSource.LoadIblPrefilterSpecular(), "ibl_prefilter_specular"));
        var prefilterBindGroupLayout = world.OwnWgpu(IblPrefilterBindGroupLayout.Create(deviceHandle));
        var prefilterPipelineLayout = world.OwnWgpu(IblPrefilterBindGroupLayout.CreatePipelineLayout(
            deviceHandle, prefilterBindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var prefilterPipeline = world.OwnWgpu(CreateFullscreenPipeline(
            deviceHandle,
            prefilterShader.GetWgpu<WGPUShaderModule>(),
            prefilterPipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            WGPUTextureFormat.RGBA16Float));

        var brdfLutShader = world.OwnWgpu(Wgpu.CreateWgslShaderModule(
            deviceHandle, SceneShaderSource.LoadIblBrdfLut(), "ibl_brdf_lut"));
        var brdfLutPipelineLayout = world.OwnWgpu(CreateEmptyPipelineLayout(deviceHandle));
        var brdfLutPipeline = world.OwnWgpu(CreateFullscreenPipeline(
            deviceHandle,
            brdfLutShader.GetWgpu<WGPUShaderModule>(),
            brdfLutPipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            WGPUTextureFormat.RG16Float));

        return new IblPrecomputePipelines(prefilterPipeline, prefilterBindGroupLayout, brdfLutPipeline);
    }

    private static WgpuHandle<WGPUPipelineLayout> CreateEmptyPipelineLayout(WgpuHandle<WGPUDevice> device)
    {
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 0;
        descriptor.BindGroupLayouts = null;
        return Wgpu.CreatePipelineLayout(device, in descriptor);
    }

    private static WgpuHandle<WGPURenderPipeline> CreateFullscreenPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat colorFormat)
    {
        var vertexEntryPoint = "vertex"u8;
        var fragmentEntryPoint = "fragment"u8;
        fixed (byte* vertexEntry = vertexEntryPoint)
        fixed (byte* fragmentEntry = fragmentEntryPoint) {
            var colorTarget = WGPUColorTargetState.Default;
            colorTarget.Format = colorFormat;
            colorTarget.WriteMask = WGPUColorWriteMask.All;

            var fragment = WGPUFragmentState.Default;
            fragment.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
            fragment.EntryPoint = new WGPUStringView { Data = fragmentEntry, Length = (nuint)fragmentEntryPoint.Length };
            fragment.TargetCount = 1;
            fragment.Targets = &colorTarget;

            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.DangerousGetHandle();
            descriptor.Vertex = WGPUVertexState.Default;
            descriptor.Vertex.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
            descriptor.Vertex.EntryPoint = new WGPUStringView { Data = vertexEntry, Length = (nuint)vertexEntryPoint.Length };
            descriptor.Vertex.BufferCount = 0;
            descriptor.Vertex.Buffers = null;
            descriptor.Primitive = WGPUPrimitiveState.Default;
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
            descriptor.Primitive.CullMode = WGPUCullMode.None;
            descriptor.DepthStencil = null;
            descriptor.Multisample = WGPUMultisampleState.Default;
            descriptor.Fragment = &fragment;
            return Wgpu.CreateRenderPipeline(device, in descriptor);
        }
    }
}

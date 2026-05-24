namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static WgpuHandle<WGPUShaderModule> CreateShaderModule(
        WgpuHandle<WGPUDevice> device,
        in WGPUShaderModuleDescriptor descriptor)
    {
        fixed (WGPUShaderModuleDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUShaderModule>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateShaderModule(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUShaderModule> CreateWgslShaderModule(
        WgpuHandle<WGPUDevice> device,
        string wgsl,
        string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wgsl);

        using var labelString = WgpuOwnedString.Create(label);
        using var wgslString = WgpuOwnedString.Create(wgsl);

        var source = new WGPUShaderSourceWGSL {
            Chain = new WGPUChainedStruct {
                Next = null,
                SType = WGPUSType.ShaderSourceWGSL,
            },
            Code = wgslString.View,
        };

        var descriptor = new WGPUShaderModuleDescriptor {
            NextInChain = &source.Chain,
            Label = labelString.View,
        };

        return CreateShaderModule(device, in descriptor);
    }

    public static WgpuHandle<WGPUBindGroupLayout> CreateBindGroupLayout(
        WgpuHandle<WGPUDevice> device,
        in WGPUBindGroupLayoutDescriptor descriptor)
    {
        fixed (WGPUBindGroupLayoutDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUBindGroupLayout>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateBindGroupLayout(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device,
        in WGPUPipelineLayoutDescriptor descriptor)
    {
        fixed (WGPUPipelineLayoutDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUPipelineLayout>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreatePipelineLayout(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUDevice> device,
        in WGPUBindGroupDescriptor descriptor)
    {
        fixed (WGPUBindGroupDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUBindGroup>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateBindGroup(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        in WGPURenderPipelineDescriptor descriptor)
    {
        fixed (WGPURenderPipelineDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPURenderPipeline>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateRenderPipeline(GetPointer(device), descriptorPtr));
        }
    }

    public static WgpuHandle<WGPUComputePipeline> CreateComputePipeline(
        WgpuHandle<WGPUDevice> device,
        in WGPUComputePipelineDescriptor descriptor)
    {
        fixed (WGPUComputePipelineDescriptor* descriptorPtr = &descriptor) {
            return WgpuHandle<WGPUComputePipeline>.FromPointer(
                WgpuUnsafe.wgpuDeviceCreateComputePipeline(GetPointer(device), descriptorPtr));
        }
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;

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

    public static WgpuHandle<WGPUShaderModule> CreateSpirvShaderModule(
        WgpuHandle<WGPUDevice> device,
        ReadOnlySpan<byte> spirv,
        string? label = null)
    {
        if (OperatingSystem.IsBrowser()) {
            throw new PlatformNotSupportedException(
                "Browser WebGPU accepts WGSL shader modules, not SPIR-V modules.");
        }
        if (spirv.Length < 20 || spirv.Length % sizeof(uint) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(spirv) != 0x07230203) {
            throw new ArgumentException("The shader is not a valid SPIR-V binary module.", nameof(spirv));
        }
        if (!BitConverter.IsLittleEndian) {
            throw new PlatformNotSupportedException("SPIR-V bytecode loading requires a little-endian host.");
        }
        return CreateSpirvShaderModule(device, MemoryMarshal.Cast<byte, uint>(spirv), label);
    }

    public static WgpuHandle<WGPUShaderModule> CreateSpirvShaderModule(
        WgpuHandle<WGPUDevice> device,
        ReadOnlySpan<uint> spirv,
        string? label = null)
    {
        if (OperatingSystem.IsBrowser()) {
            throw new PlatformNotSupportedException(
                "Browser WebGPU accepts WGSL shader modules, not SPIR-V modules.");
        }
        if (spirv.Length < 5 || spirv[0] != 0x07230203) {
            throw new ArgumentException("The shader is not a valid SPIR-V binary module.", nameof(spirv));
        }

        using var labelString = WgpuOwnedString.Create(label);
        fixed (uint* code = spirv) {
            var source = new WGPUShaderSourceSPIRV {
                Chain = new WGPUChainedStruct {
                    Next = null,
                    SType = WGPUSType.ShaderSourceSPIRV,
                },
                CodeSize = checked((uint)spirv.Length),
                Code = code,
            };
            var descriptor = new WGPUShaderModuleDescriptor {
                NextInChain = &source.Chain,
                Label = labelString.View,
            };
            return CreateShaderModule(device, in descriptor);
        }
    }

    public static WgpuHandle<WGPUShaderModule> CreatePortableShaderModule(
        WgpuHandle<WGPUDevice> device,
        ReadOnlySpan<byte> spirv,
        string wgsl,
        string? label = null) =>
        OperatingSystem.IsBrowser()
            ? CreateWgslShaderModule(device, wgsl, label)
            : CreateSpirvShaderModule(device, spirv, label);

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

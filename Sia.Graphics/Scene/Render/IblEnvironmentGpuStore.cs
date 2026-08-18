using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct IblShGpu(
    float4 Sh0, float4 Sh1, float4 Sh2, float4 Sh3, float4 Sh4,
    float4 Sh5, float4 Sh6, float4 Sh7, float4 Sh8)
{
    public const int Stride = 144;

    public static IblShGpu FromCoefficients(float4[] coefficients) => new(
        coefficients[0], coefficients[1], coefficients[2], coefficients[3], coefficients[4],
        coefficients[5], coefficients[6], coefficients[7], coefficients[8]);
}

public sealed class IblEnvironmentGpuStore
{
    public const uint PrefilteredResolution = 128;
    public const int PrefilteredMipCount = 7;
    public const uint BrdfLutResolution = 128;

    private Entity _prefilteredTexture;
    private Entity _prefilteredSamplingView;
    private Entity _prefilteredSampler;
    private Entity _brdfLutTexture;
    private Entity _brdfLutView;
    private Entity _brdfLutSampler;
    private Entity _shBuffer;
    private bool _resourcesCreated;

    public Entity PrefilteredTexture => _prefilteredTexture;

    public Entity PrefilteredSamplingView => _prefilteredSamplingView;

    public Entity PrefilteredSampler => _prefilteredSampler;

    public Entity BrdfLutTexture => _brdfLutTexture;

    public Entity BrdfLutView => _brdfLutView;

    public Entity BrdfLutSampler => _brdfLutSampler;

    public Entity ShBuffer => _shBuffer;

    public bool IsValid => _resourcesCreated;

    public bool EnsureCapacity(in GpuFrame frame)
    {
        if (_resourcesCreated) {
            return false;
        }

        _prefilteredTexture = frame.World.CreateWgpuTexture(frame.Device, new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D { Width = PrefilteredResolution, Height = PrefilteredResolution, DepthOrArrayLayers = 6 },
            Format = WGPUTextureFormat.RGBA16Float,
            MipLevelCount = PrefilteredMipCount,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null
        });
        _prefilteredSamplingView = frame.World.CreateWgpuTextureView(_prefilteredTexture, new WGPUTextureViewDescriptor {
            NextInChain = null,
            Label = default,
            Format = WGPUTextureFormat.RGBA16Float,
            Dimension = WGPUTextureViewDimension.Cube,
            BaseMipLevel = 0,
            MipLevelCount = PrefilteredMipCount,
            BaseArrayLayer = 0,
            ArrayLayerCount = 6,
            Aspect = WGPUTextureAspect.All,
            Usage = WGPUTextureUsage.TextureBinding
        });
        _prefilteredSampler = frame.World.CreateWgpuSampler(frame.Device, new WGPUSamplerDescriptor {
            NextInChain = null,
            Label = default,
            AddressModeU = WGPUAddressMode.ClampToEdge,
            AddressModeV = WGPUAddressMode.ClampToEdge,
            AddressModeW = WGPUAddressMode.ClampToEdge,
            MagFilter = WGPUFilterMode.Linear,
            MinFilter = WGPUFilterMode.Linear,
            MipmapFilter = WGPUMipmapFilterMode.Linear,
            LodMinClamp = 0,
            LodMaxClamp = PrefilteredMipCount,
            Compare = WGPUCompareFunction.Undefined,
            MaxAnisotropy = 1
        });

        _brdfLutTexture = frame.World.CreateWgpuTexture(frame.Device, new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D { Width = BrdfLutResolution, Height = BrdfLutResolution, DepthOrArrayLayers = 1 },
            Format = WGPUTextureFormat.RG16Float,
            MipLevelCount = 1,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null
        });
        _brdfLutView = frame.World.CreateWgpuTextureView(_brdfLutTexture, new WGPUTextureViewDescriptor {
            NextInChain = null,
            Label = default,
            Format = WGPUTextureFormat.RG16Float,
            Dimension = WGPUTextureViewDimension._2D,
            BaseMipLevel = 0,
            MipLevelCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
            Aspect = WGPUTextureAspect.All,
            Usage = WGPUTextureUsage.TextureBinding
        });
        _brdfLutSampler = frame.World.CreateWgpuSampler(frame.Device, new WGPUSamplerDescriptor {
            NextInChain = null,
            Label = default,
            AddressModeU = WGPUAddressMode.ClampToEdge,
            AddressModeV = WGPUAddressMode.ClampToEdge,
            AddressModeW = WGPUAddressMode.ClampToEdge,
            MagFilter = WGPUFilterMode.Linear,
            MinFilter = WGPUFilterMode.Linear,
            MipmapFilter = WGPUMipmapFilterMode.Nearest,
            LodMinClamp = 0,
            LodMaxClamp = 1,
            Compare = WGPUCompareFunction.Undefined,
            MaxAnisotropy = 1
        });

        _shBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc,
            Size = IblShGpu.Stride,
            MappedAtCreation = 0
        });

        _resourcesCreated = true;
        return true;
    }

    public void UploadSh(in GpuFrame frame, float4[] coefficients) =>
        Wgpu.WriteBuffer(
            frame.Queue.GetWgpu<WGPUQueue>(), _shBuffer.GetWgpu<WGPUBuffer>(), 0,
            [IblShGpu.FromCoefficients(coefficients)]);
}

using Sia;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed class ShadowAtlasGpuStore
{
    private Entity _texture;
    private Entity _samplingView;
    private Entity _sampler;
    private uint _configuredResolution;
    private int _configuredLayerCount;

    public Entity Texture => _texture;

    public Entity SamplingView => _samplingView;

    public Entity Sampler => _sampler;

    public bool IsValid => _texture.IsValid;

    public bool EnsureCapacity(in GpuFrame frame, ShadowAtlasConfig config)
    {
        if (_texture.IsValid
            && config.TileResolution == _configuredResolution
            && config.LayerCount == _configuredLayerCount) {
            return false;
        }

        if (_sampler.IsValid) {
            _sampler.Destroy();
        }
        if (_samplingView.IsValid) {
            _samplingView.Destroy();
        }
        if (_texture.IsValid) {
            _texture.Destroy();
        }

        _texture = frame.World.CreateWgpuTexture(frame.Device, new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D {
                Width = config.TileResolution,
                Height = config.TileResolution,
                DepthOrArrayLayers = (uint)config.LayerCount
            },
            Format = WGPUTextureFormat.Depth32Float,
            MipLevelCount = 1,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null
        });

        _samplingView = frame.World.CreateWgpuTextureView(_texture, new WGPUTextureViewDescriptor {
            NextInChain = null,
            Label = default,
            Format = WGPUTextureFormat.Depth32Float,
            Dimension = WGPUTextureViewDimension._2DArray,
            BaseMipLevel = 0,
            MipLevelCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = (uint)config.LayerCount,
            Aspect = WGPUTextureAspect.DepthOnly,
            Usage = WGPUTextureUsage.TextureBinding
        });

        _sampler = frame.World.CreateWgpuSampler(frame.Device, new WGPUSamplerDescriptor {
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
            Compare = WGPUCompareFunction.LessEqual,
            MaxAnisotropy = 1
        });

        _configuredResolution = config.TileResolution;
        _configuredLayerCount = config.LayerCount;
        return true;
    }
}

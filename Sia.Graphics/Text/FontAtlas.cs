using Sia;
using Sia.Graphics.UI;
using Sia.WebGPU;

namespace Sia.Graphics.Text;

public sealed class FontAtlas
{
    private readonly DynamicTextureAtlasBuilder _packer;
    private readonly Dictionary<ushort, GlyphAtlasLocation> _glyphs = [];
    private readonly byte[] _pixels;
    private bool _dirty;
    private bool _gpuCreated;

    private Entity _texture;
    private Entity _textureView;
    private Entity _bindGroup;

    public int Width { get; }
    public int Height { get; }

    public FontAtlas(int width, int height)
    {
        Width = width;
        Height = height;
        _packer = new DynamicTextureAtlasBuilder(width, height);
        _pixels = new byte[width * height * 4];
    }

    public bool TryGetGlyph(ushort glyphId, out GlyphAtlasLocation location) =>
        _glyphs.TryGetValue(glyphId, out location);

    public bool TryAddGlyph(ushort glyphId, RasterizedGlyph glyph, out GlyphAtlasLocation location)
    {
        if (glyph.Width == 0 || glyph.Height == 0) {
            location = new GlyphAtlasLocation(0, 0, 0, 0, glyph.OriginX, glyph.OriginY);
            _glyphs[glyphId] = location;
            return true;
        }

        if (!_packer.TryAllocate(glyph.Width, glyph.Height, out var x, out var y)) {
            location = default;
            return false;
        }

        for (var row = 0; row < glyph.Height; row++) {
            for (var col = 0; col < glyph.Width; col++) {
                var coverage = glyph.Coverage[row * glyph.Width + col];
                var dst = ((y + row) * Width + (x + col)) * 4;
                _pixels[dst + 0] = 255;
                _pixels[dst + 1] = 255;
                _pixels[dst + 2] = 255;
                _pixels[dst + 3] = coverage;
            }
        }
        _dirty = true;

        location = new GlyphAtlasLocation(x, y, glyph.Width, glyph.Height, glyph.OriginX, glyph.OriginY);
        _glyphs[glyphId] = location;
        return true;
    }

    public WgpuHandle<WGPUBindGroup> GetOrCreateBindGroup(World world, UiPipeline pipeline)
    {
        if (!_gpuCreated) {
            CreateGpuResources(world, pipeline);
            _gpuCreated = true;
        }
        if (_dirty) {
            UploadPixels(pipeline);
            _dirty = false;
        }
        return _bindGroup.GetWgpu<WGPUBindGroup>();
    }

    private void CreateGpuResources(World world, UiPipeline pipeline)
    {
        _texture = world.CreateWgpuTexture(pipeline.Device, new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D { Width = (uint)Width, Height = (uint)Height, DepthOrArrayLayers = 1 },
            Format = WGPUTextureFormat.RGBA8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null
        });

        _textureView = world.CreateWgpuTextureView(_texture, WGPUTextureViewDescriptor.Default with {
            Format = WGPUTextureFormat.RGBA8Unorm,
            Dimension = WGPUTextureViewDimension._2D,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Aspect = WGPUTextureAspect.All
        });

        _bindGroup = world.OwnWgpu(CreateBindGroup(pipeline));
    }

    private unsafe WgpuHandle<WGPUBindGroup> CreateBindGroup(UiPipeline pipeline)
    {
        var layout = pipeline.TextureBindGroupLayout.GetWgpu<WGPUBindGroupLayout>();
        var textureView = _textureView.GetWgpu<WGPUTextureView>();
        var sampler = pipeline.DefaultSampler.GetWgpu<WGPUSampler>();

        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[2];
        entries[0] = WGPUBindGroupEntry.Default;
        entries[0].Binding = 0;
        entries[0].TextureView = (WGPUTextureView*)textureView.DangerousGetHandle();
        entries[1] = WGPUBindGroupEntry.Default;
        entries[1].Binding = 1;
        entries[1].Sampler = (WGPUSampler*)sampler.DangerousGetHandle();

        fixed (WGPUBindGroupEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
            descriptor.EntryCount = 2;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroup(pipeline.Device.GetWgpu<WGPUDevice>(), in descriptor);
        }
    }

    private unsafe void UploadPixels(UiPipeline pipeline)
    {
        var queue = pipeline.Queue.GetWgpu<WGPUQueue>();
        var layout = new WGPUTexelCopyBufferLayout {
            Offset = 0,
            BytesPerRow = (uint)(Width * 4),
            RowsPerImage = (uint)Height
        };
        var copyTexture = new WGPUTexelCopyTextureInfo {
            Texture = (WGPUTexture*)_texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
            MipLevel = 0,
            Origin = default,
            Aspect = WGPUTextureAspect.All
        };
        var extent = new WGPUExtent3D { Width = (uint)Width, Height = (uint)Height, DepthOrArrayLayers = 1 };

        fixed (byte* pixelsPtr = _pixels) {
            WgpuUnsafe.wgpuQueueWriteTexture(
                (WGPUQueue*)queue.DangerousGetHandle(),
                &copyTexture, pixelsPtr, (nuint)_pixels.Length, &layout, &extent);
        }
    }
}

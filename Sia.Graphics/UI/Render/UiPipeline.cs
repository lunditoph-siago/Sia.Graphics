using Sia;
using Sia.Graphics.Text;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public sealed unsafe class UiPipeline
{
    internal Entity Device { get; }
    internal Entity Queue { get; }
    internal Entity RenderPipeline { get; }
    internal Entity ViewUniformBuffer { get; }
    internal Entity BindGroupLayout { get; }
    private Entity TextureArray { get; }
    private Entity TextureArrayView { get; }
    private Entity Sampler { get; }

    private UiPipeline(
        Entity device,
        Entity queue,
        Entity renderPipeline,
        Entity viewUniformBuffer,
        Entity bindGroupLayout,
        Entity textureArray,
        Entity textureArrayView,
        Entity sampler)
    {
        Device = device;
        Queue = queue;
        RenderPipeline = renderPipeline;
        ViewUniformBuffer = viewUniformBuffer;
        BindGroupLayout = bindGroupLayout;
        TextureArray = textureArray;
        TextureArrayView = textureArrayView;
        Sampler = sampler;
    }

    public static UiPipeline Create(World world, Entity device, Entity queue, WGPUTextureFormat targetFormat)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var shaderModule = world.OwnWgpu(
            Wgpu.CreateWgslShaderModule(deviceHandle, UiShaderSource.Load(), "ui_node"));
        var bindGroupLayout = world.OwnWgpu(CreateBindGroupLayout(deviceHandle));
        var pipelineLayout = world.OwnWgpu(
            CreatePipelineLayout(deviceHandle, bindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            targetFormat));
        var viewUniformBuffer = world.CreateWgpuBuffer(device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
            Size = 64,
            MappedAtCreation = 0
        });
        var textureArray = world.CreateWgpuTexture(device, new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D {
                Width = FontAtlasSet.AtlasSize,
                Height = FontAtlasSet.AtlasSize,
                DepthOrArrayLayers = FontAtlasSet.MaxAtlasLayers
            },
            Format = WGPUTextureFormat.RGBA8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null
        });
        var textureArrayView = world.CreateWgpuTextureView(
            textureArray,
            WGPUTextureViewDescriptor.Default with {
                Format = WGPUTextureFormat.RGBA8Unorm,
                Dimension = WGPUTextureViewDimension._2DArray,
                MipLevelCount = 1,
                ArrayLayerCount = FontAtlasSet.MaxAtlasLayers,
                Aspect = WGPUTextureAspect.All
            });
        var sampler = world.CreateWgpuSampler(device, SamplerDescriptor());
        return new UiPipeline(
            device,
            queue,
            renderPipeline,
            viewUniformBuffer,
            bindGroupLayout,
            textureArray,
            textureArrayView,
            sampler);
    }

    internal WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUBuffer> primitiveBuffer,
        ulong primitiveBufferSize)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[4];
        entries[0] = WGPUBindGroupEntry.Default;
        entries[0].Binding = 0;
        entries[0].Buffer = (WGPUBuffer*)ViewUniformBuffer.GetWgpu<WGPUBuffer>().DangerousGetHandle();
        entries[0].Size = 64;
        entries[1] = WGPUBindGroupEntry.Default;
        entries[1].Binding = 1;
        entries[1].Buffer = (WGPUBuffer*)primitiveBuffer.DangerousGetHandle();
        entries[1].Size = primitiveBufferSize;
        entries[2] = WGPUBindGroupEntry.Default;
        entries[2].Binding = 2;
        entries[2].TextureView = (WGPUTextureView*)TextureArrayView.GetWgpu<WGPUTextureView>().DangerousGetHandle();
        entries[3] = WGPUBindGroupEntry.Default;
        entries[3].Binding = 3;
        entries[3].Sampler = (WGPUSampler*)Sampler.GetWgpu<WGPUSampler>().DangerousGetHandle();

        fixed (WGPUBindGroupEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)BindGroupLayout
                .GetWgpu<WGPUBindGroupLayout>()
                .DangerousGetHandle();
            descriptor.EntryCount = 4;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroup(Device.GetWgpu<WGPUDevice>(), in descriptor);
        }
    }

    internal void UploadAtlases(FontAtlasSet atlasSet)
    {
        foreach (var atlas in atlasSet.Atlases) {
            if (!atlas.TryTakeDirtyRegion(out var region))
                continue;

            var layout = new WGPUTexelCopyBufferLayout {
                Offset = 0,
                BytesPerRow = (uint)(atlas.Width * 4),
                RowsPerImage = (uint)atlas.Height
            };
            var destination = new WGPUTexelCopyTextureInfo {
                Texture = (WGPUTexture*)TextureArray.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                MipLevel = 0,
                Origin = new WGPUOrigin3D {
                    X = (uint)region.X,
                    Y = (uint)region.Y,
                    Z = (uint)atlas.Layer
                },
                Aspect = WGPUTextureAspect.All
            };
            var extent = new WGPUExtent3D {
                Width = (uint)region.Width,
                Height = (uint)region.Height,
                DepthOrArrayLayers = 1
            };
            var sourceOffset = (region.Y * atlas.Width + region.X) * 4;
            var sourceSize = (region.Height - 1) * atlas.Width * 4 + region.Width * 4;
            fixed (byte* pixels = atlas.Pixels) {
                WgpuUnsafe.wgpuQueueWriteTexture(
                    (WGPUQueue*)Queue.GetWgpu<WGPUQueue>().DangerousGetHandle(),
                    &destination,
                    pixels + sourceOffset,
                    (nuint)sourceSize,
                    &layout,
                    &extent);
            }
        }
    }

    private static WgpuHandle<WGPUBindGroupLayout> CreateBindGroupLayout(WgpuHandle<WGPUDevice> device)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[4];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = 0;
        entries[0].Visibility = WGPUShaderStage.Vertex;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;
        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = 1;
        entries[1].Visibility = WGPUShaderStage.Vertex;
        entries[1].Buffer = WGPUBufferBindingLayout.Default;
        entries[1].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;
        entries[2] = WGPUBindGroupLayoutEntry.Default;
        entries[2].Binding = 2;
        entries[2].Visibility = WGPUShaderStage.Fragment;
        entries[2].Texture = WGPUTextureBindingLayout.Default;
        entries[2].Texture.SampleType = WGPUTextureSampleType.Float;
        entries[2].Texture.ViewDimension = WGPUTextureViewDimension._2DArray;
        entries[3] = WGPUBindGroupLayoutEntry.Default;
        entries[3].Binding = 3;
        entries[3].Visibility = WGPUShaderStage.Fragment;
        entries[3].Sampler = WGPUSamplerBindingLayout.Default;
        entries[3].Sampler.Type = WGPUSamplerBindingType.Filtering;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = 4;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroupLayout(device, in descriptor);
        }
    }

    private static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> layout)
    {
        var layoutPtr = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 1;
        descriptor.BindGroupLayouts = &layoutPtr;
        return Wgpu.CreatePipelineLayout(device, in descriptor);
    }

    private static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat targetFormat)
    {
        fixed (byte* vertexEntry = "vertex"u8)
        fixed (byte* fragmentEntry = "fragment"u8) {
            var blend = WGPUBlendState.Default;
            blend.Color.Operation = WGPUBlendOperation.Add;
            blend.Color.SrcFactor = WGPUBlendFactor.SrcAlpha;
            blend.Color.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
            blend.Alpha.Operation = WGPUBlendOperation.Add;
            blend.Alpha.SrcFactor = WGPUBlendFactor.One;
            blend.Alpha.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;

            var colorTarget = WGPUColorTargetState.Default;
            colorTarget.Format = targetFormat;
            colorTarget.Blend = &blend;
            colorTarget.WriteMask = WGPUColorWriteMask.All;

            var fragment = WGPUFragmentState.Default;
            fragment.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
            fragment.EntryPoint = new WGPUStringView { Data = fragmentEntry, Length = 8 };
            fragment.TargetCount = 1;
            fragment.Targets = &colorTarget;

            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.DangerousGetHandle();
            descriptor.Vertex = WGPUVertexState.Default;
            descriptor.Vertex.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
            descriptor.Vertex.EntryPoint = new WGPUStringView { Data = vertexEntry, Length = 6 };
            descriptor.Primitive = WGPUPrimitiveState.Default;
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
            descriptor.Primitive.CullMode = WGPUCullMode.None;
            descriptor.Multisample = WGPUMultisampleState.Default;
            descriptor.Fragment = &fragment;
            return Wgpu.CreateRenderPipeline(device, in descriptor);
        }
    }

    private static WGPUSamplerDescriptor SamplerDescriptor()
    {
        var descriptor = WGPUSamplerDescriptor.Default;
        descriptor.AddressModeU = WGPUAddressMode.ClampToEdge;
        descriptor.AddressModeV = WGPUAddressMode.ClampToEdge;
        descriptor.AddressModeW = WGPUAddressMode.ClampToEdge;
        descriptor.MagFilter = WGPUFilterMode.Linear;
        descriptor.MinFilter = WGPUFilterMode.Linear;
        descriptor.MipmapFilter = WGPUMipmapFilterMode.Nearest;
        descriptor.LodMinClamp = 0f;
        descriptor.LodMaxClamp = 1f;
        descriptor.MaxAnisotropy = 1;
        return descriptor;
    }
}

using Sia;
using Sia.Graphics.Text;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public sealed unsafe class UiPipeline
{
    private const int k_StorageBindGroupEntryCount = 5;
    private const int k_CompatibilityBindGroupEntryCount = 3;
    private const int k_VertexAttributeCount = 7;
    private const int k_InitialTextureArrayLayers = 4;

    internal Entity Device { get; }
    internal Entity Queue { get; }
    internal Entity RenderPipeline { get; }
    internal Entity ViewUniformBuffer { get; }
    internal Entity BindGroupLayout { get; }
    internal bool UsesVertexStorage { get; }
    private Entity TextureArray { get; set; }
    private Entity TextureArrayView { get; set; }
    private Entity Sampler { get; }
    private int TextureArrayLayers { get; set; }
    internal int TextureVersion { get; private set; }

    private UiPipeline(
        Entity device,
        Entity queue,
        Entity renderPipeline,
        Entity viewUniformBuffer,
        Entity bindGroupLayout,
        Entity textureArray,
        Entity textureArrayView,
        Entity sampler,
        int textureArrayLayers,
        bool usesVertexStorage)
    {
        Device = device;
        Queue = queue;
        RenderPipeline = renderPipeline;
        ViewUniformBuffer = viewUniformBuffer;
        BindGroupLayout = bindGroupLayout;
        TextureArray = textureArray;
        TextureArrayView = textureArrayView;
        Sampler = sampler;
        TextureArrayLayers = textureArrayLayers;
        UsesVertexStorage = usesVertexStorage;
    }

    public static UiPipeline Create(World world, Entity device, Entity queue, WGPUTextureFormat targetFormat)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var usesVertexStorage = Wgpu.Backend != WgpuBackendKind.BrowserGles;
        var shaderModule = world.OwnWgpu(
            Wgpu.CreateWgslShaderModule(deviceHandle, UiShaderSource.Load(), "ui_node"));
        var vertexShaderModule = usesVertexStorage
            ? shaderModule
            : world.OwnWgpu(Wgpu.CreateWgslShaderModule(
                deviceHandle,
                UiShaderSource.LoadCompatibilityVertex(),
                "ui_node_compat_vertex"));
        var bindGroupLayout = world.OwnWgpu(
            CreateBindGroupLayout(deviceHandle, usesVertexStorage));
        var pipelineLayout = world.OwnWgpu(
            CreatePipelineLayout(deviceHandle, bindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            vertexShaderModule.GetWgpu<WGPUShaderModule>(),
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            targetFormat,
            usesVertexStorage));
        var viewUniformBuffer = world.CreateWgpuBuffer(device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
            Size = UiOrthographicProjection.k_UniformByteSize,
            MappedAtCreation = 0
        });
        var (textureArray, textureArrayView) = CreateTextureArray(
            world,
            device,
            k_InitialTextureArrayLayers);
        var sampler = world.CreateWgpuSampler(device, SamplerDescriptor());
        return new UiPipeline(
            device,
            queue,
            renderPipeline,
            viewUniformBuffer,
            bindGroupLayout,
            textureArray,
            textureArrayView,
            sampler,
            k_InitialTextureArrayLayers,
            usesVertexStorage);
    }

    internal WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUBuffer> primitiveBuffer,
        ulong primitiveBufferSize,
        WgpuHandle<WGPUBuffer> paintOrderBuffer,
        ulong paintOrderBufferSize)
    {
        Span<WGPUBindGroupEntry> entries =
            stackalloc WGPUBindGroupEntry[k_StorageBindGroupEntryCount];
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = 0,
            Buffer = (WGPUBuffer*)ViewUniformBuffer.GetWgpu<WGPUBuffer>().DangerousGetHandle(),
            Size = UiOrthographicProjection.k_UniformByteSize
        };
        entries[1] = WGPUBindGroupEntry.Default with {
            Binding = 1,
            Buffer = (WGPUBuffer*)primitiveBuffer.DangerousGetHandle(),
            Size = primitiveBufferSize
        };
        entries[2] = WGPUBindGroupEntry.Default with {
            Binding = 2,
            Buffer = (WGPUBuffer*)paintOrderBuffer.DangerousGetHandle(),
            Size = paintOrderBufferSize
        };
        entries[3] = WGPUBindGroupEntry.Default with {
            Binding = 3,
            TextureView = (WGPUTextureView*)TextureArrayView
                .GetWgpu<WGPUTextureView>()
                .DangerousGetHandle()
        };
        entries[4] = WGPUBindGroupEntry.Default with {
            Binding = 4,
            Sampler = (WGPUSampler*)Sampler.GetWgpu<WGPUSampler>().DangerousGetHandle()
        };

        return CreateBindGroup(entries);
    }

    internal WgpuHandle<WGPUBindGroup> CreateCompatibilityBindGroup()
    {
        Span<WGPUBindGroupEntry> entries =
            stackalloc WGPUBindGroupEntry[k_CompatibilityBindGroupEntryCount];
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = 0,
            Buffer = (WGPUBuffer*)ViewUniformBuffer.GetWgpu<WGPUBuffer>().DangerousGetHandle(),
            Size = UiOrthographicProjection.k_UniformByteSize
        };
        entries[1] = WGPUBindGroupEntry.Default with {
            Binding = 3,
            TextureView = (WGPUTextureView*)TextureArrayView
                .GetWgpu<WGPUTextureView>()
                .DangerousGetHandle()
        };
        entries[2] = WGPUBindGroupEntry.Default with {
            Binding = 4,
            Sampler = (WGPUSampler*)Sampler.GetWgpu<WGPUSampler>().DangerousGetHandle()
        };

        return CreateBindGroup(entries);
    }

    private WgpuHandle<WGPUBindGroup> CreateBindGroup(Span<WGPUBindGroupEntry> entries)
    {
        fixed (WGPUBindGroupEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)BindGroupLayout
                .GetWgpu<WGPUBindGroupLayout>()
                .DangerousGetHandle();
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroup(Device.GetWgpu<WGPUDevice>(), in descriptor);
        }
    }

    internal void UploadAtlases(World world, FontAtlasSet atlasSet)
    {
        EnsureTextureArrayCapacity(world, atlasSet.Atlases.Count + 1);
        foreach (var atlas in atlasSet.Atlases) {
            if (!atlas.TryTakeDirtyRegion(out var region))
                continue;

            var layout = new WGPUTexelCopyBufferLayout {
                Offset = 0,
                BytesPerRow = (uint)atlas.Width,
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
            var sourceOffset = region.Y * atlas.Width + region.X;
            var sourceSize = (region.Height - 1) * atlas.Width + region.Width;
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

    private void EnsureTextureArrayCapacity(World world, int requiredLayers)
    {
        if (requiredLayers <= TextureArrayLayers)
            return;

        var newLayers = TextureArrayLayers;
        while (newLayers < requiredLayers)
            newLayers = System.Math.Min(newLayers * 2, FontAtlasSet.k_MaxAtlasLayers);
        if (newLayers < requiredLayers)
            throw new InvalidOperationException("The UI font texture array is full.");

        var (texture, view) = CreateTextureArray(world, Device, newLayers);
        CopyTextureArray(TextureArray, texture, TextureArrayLayers);
        TextureArrayView.Destroy();
        TextureArray.Destroy();
        TextureArray = texture;
        TextureArrayView = view;
        TextureArrayLayers = newLayers;
        TextureVersion++;
    }

    private void CopyTextureArray(Entity source, Entity destination, int layers)
    {
        var encoder = Wgpu.CreateCommandEncoder(Device.GetWgpu<WGPUDevice>());
        var commandBuffer = default(WgpuHandle<WGPUCommandBuffer>);
        try {
            var sourceInfo = new WGPUTexelCopyTextureInfo {
                Texture = (WGPUTexture*)source.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                MipLevel = 0,
                Origin = default,
                Aspect = WGPUTextureAspect.All
            };
            var destinationInfo = new WGPUTexelCopyTextureInfo {
                Texture = (WGPUTexture*)destination.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                MipLevel = 0,
                Origin = default,
                Aspect = WGPUTextureAspect.All
            };
            var extent = new WGPUExtent3D {
                Width = FontAtlasSet.k_AtlasSize,
                Height = FontAtlasSet.k_AtlasSize,
                DepthOrArrayLayers = (uint)layers
            };
            WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture(
                (WGPUCommandEncoder*)encoder.DangerousGetHandle(),
                &sourceInfo,
                &destinationInfo,
                &extent);
            var descriptor = WGPUCommandBufferDescriptor.Default;
            commandBuffer = Wgpu.FinishCommandEncoder(encoder, in descriptor);
            Wgpu.Submit(Queue.GetWgpu<WGPUQueue>(), [commandBuffer]);
        }
        finally {
            Wgpu.Release(ref commandBuffer);
            Wgpu.Release(ref encoder);
        }
    }

    private static (Entity Texture, Entity View) CreateTextureArray(
        World world,
        Entity device,
        int layers)
    {
        var texture = world.CreateWgpuTexture(device, new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.TextureBinding
                | WGPUTextureUsage.CopySrc
                | WGPUTextureUsage.CopyDst,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D {
                Width = FontAtlasSet.k_AtlasSize,
                Height = FontAtlasSet.k_AtlasSize,
                DepthOrArrayLayers = (uint)layers
            },
            Format = WGPUTextureFormat.R8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null
        });
        var view = world.CreateWgpuTextureView(
            texture,
            WGPUTextureViewDescriptor.Default with {
                Format = WGPUTextureFormat.R8Unorm,
                Dimension = WGPUTextureViewDimension._2DArray,
                MipLevelCount = 1,
                ArrayLayerCount = (uint)layers,
                Aspect = WGPUTextureAspect.All
            });
        return (texture, view);
    }

    private static WgpuHandle<WGPUBindGroupLayout> CreateBindGroupLayout(
        WgpuHandle<WGPUDevice> device,
        bool usesVertexStorage)
    {
        var entryCount = usesVertexStorage
            ? k_StorageBindGroupEntryCount
            : k_CompatibilityBindGroupEntryCount;
        Span<WGPUBindGroupLayoutEntry> entries =
            stackalloc WGPUBindGroupLayoutEntry[entryCount];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = 0;
        entries[0].Visibility = WGPUShaderStage.Vertex;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;
        var textureIndex = 1;
        var samplerIndex = 2;
        if (usesVertexStorage) {
            entries[1] = WGPUBindGroupLayoutEntry.Default;
            entries[1].Binding = 1;
            entries[1].Visibility = WGPUShaderStage.Vertex;
            entries[1].Buffer = WGPUBufferBindingLayout.Default;
            entries[1].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;
            entries[2] = WGPUBindGroupLayoutEntry.Default;
            entries[2].Binding = 2;
            entries[2].Visibility = WGPUShaderStage.Vertex;
            entries[2].Buffer = WGPUBufferBindingLayout.Default;
            entries[2].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;
            textureIndex = 3;
            samplerIndex = 4;
        }
        entries[textureIndex] = WGPUBindGroupLayoutEntry.Default;
        entries[textureIndex].Binding = 3;
        entries[textureIndex].Visibility = WGPUShaderStage.Fragment;
        entries[textureIndex].Texture = WGPUTextureBindingLayout.Default;
        entries[textureIndex].Texture.SampleType = WGPUTextureSampleType.Float;
        entries[textureIndex].Texture.ViewDimension = WGPUTextureViewDimension._2DArray;
        entries[samplerIndex] = WGPUBindGroupLayoutEntry.Default;
        entries[samplerIndex].Binding = 4;
        entries[samplerIndex].Visibility = WGPUShaderStage.Fragment;
        entries[samplerIndex].Sampler = WGPUSamplerBindingLayout.Default;
        entries[samplerIndex].Sampler.Type = WGPUSamplerBindingType.Filtering;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = (nuint)entryCount;
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
        WgpuHandle<WGPUShaderModule> vertexShaderModule,
        WgpuHandle<WGPUShaderModule> fragmentShaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat targetFormat,
        bool usesVertexStorage)
    {
        var vertexEntryPoint = "vertex"u8;
        var fragmentEntryPoint = "fragment"u8;
        fixed (byte* vertexEntry = vertexEntryPoint)
        fixed (byte* fragmentEntry = fragmentEntryPoint) {
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
            fragment.Module = (WGPUShaderModule*)fragmentShaderModule.DangerousGetHandle();
            fragment.EntryPoint = new WGPUStringView {
                Data = fragmentEntry,
                Length = (nuint)fragmentEntryPoint.Length
            };
            fragment.TargetCount = 1;
            fragment.Targets = &colorTarget;

            Span<WGPUVertexAttribute> attributes = stackalloc WGPUVertexAttribute[k_VertexAttributeCount];
            attributes[0] = VertexAttribute(WGPUVertexFormat.Float32x4, 0, 0);
            attributes[1] = VertexAttribute(WGPUVertexFormat.Float32x4, 16, 1);
            attributes[2] = VertexAttribute(WGPUVertexFormat.Float32x4, 32, 2);
            attributes[3] = VertexAttribute(WGPUVertexFormat.Uint32x2, 48, 3);
            attributes[4] = VertexAttribute(WGPUVertexFormat.Uint32x2, 56, 4);
            attributes[5] = VertexAttribute(WGPUVertexFormat.Float32x4, 64, 5);
            attributes[6] = VertexAttribute(WGPUVertexFormat.Uint32, 80, 6);

            fixed (WGPUVertexAttribute* attributesPtr = attributes) {
                var vertexBuffer = WGPUVertexBufferLayout.Default;
                vertexBuffer.StepMode = WGPUVertexStepMode.Instance;
                vertexBuffer.ArrayStride = 96;
                vertexBuffer.AttributeCount = k_VertexAttributeCount;
                vertexBuffer.Attributes = attributesPtr;

                var descriptor = WGPURenderPipelineDescriptor.Default;
                descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.DangerousGetHandle();
                descriptor.Vertex = WGPUVertexState.Default;
                descriptor.Vertex.Module = (WGPUShaderModule*)vertexShaderModule.DangerousGetHandle();
                descriptor.Vertex.EntryPoint = new WGPUStringView {
                    Data = vertexEntry,
                    Length = (nuint)vertexEntryPoint.Length
                };
                if (!usesVertexStorage) {
                    descriptor.Vertex.BufferCount = 1;
                    descriptor.Vertex.Buffers = &vertexBuffer;
                }
                descriptor.Primitive = WGPUPrimitiveState.Default;
                descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
                descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
                descriptor.Primitive.CullMode = WGPUCullMode.None;
                descriptor.Multisample = WGPUMultisampleState.Default;
                descriptor.Fragment = &fragment;
                return Wgpu.CreateRenderPipeline(device, in descriptor);
            }
        }
    }

    private static WGPUVertexAttribute VertexAttribute(
        WGPUVertexFormat format,
        ulong offset,
        uint shaderLocation) =>
        WGPUVertexAttribute.Default with {
            Format = format,
            Offset = offset,
            ShaderLocation = shaderLocation
        };

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

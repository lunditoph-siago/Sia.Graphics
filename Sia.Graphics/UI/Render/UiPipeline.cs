using Sia;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public sealed unsafe class UiPipeline
{
    public Entity Device { get; }
    public Entity Queue { get; }
    public Entity ShaderModule { get; }
    public Entity ViewBindGroupLayout { get; }
    public Entity TextureBindGroupLayout { get; }
    public Entity PipelineLayout { get; }
    public Entity RenderPipeline { get; }
    public Entity ViewUniformBuffer { get; }
    public Entity DefaultTexture { get; }
    public Entity DefaultTextureView { get; }
    public Entity DefaultSampler { get; }
    public Entity ViewBindGroup { get; }
    public Entity DefaultTextureBindGroup { get; }

    private UiPipeline(
        Entity device, Entity queue, Entity shaderModule, Entity viewBindGroupLayout, Entity textureBindGroupLayout,
        Entity pipelineLayout, Entity renderPipeline, Entity viewUniformBuffer, Entity defaultTexture,
        Entity defaultTextureView, Entity defaultSampler, Entity viewBindGroup, Entity defaultTextureBindGroup)
    {
        Device = device;
        Queue = queue;
        ShaderModule = shaderModule;
        ViewBindGroupLayout = viewBindGroupLayout;
        TextureBindGroupLayout = textureBindGroupLayout;
        PipelineLayout = pipelineLayout;
        RenderPipeline = renderPipeline;
        ViewUniformBuffer = viewUniformBuffer;
        DefaultTexture = defaultTexture;
        DefaultTextureView = defaultTextureView;
        DefaultSampler = defaultSampler;
        ViewBindGroup = viewBindGroup;
        DefaultTextureBindGroup = defaultTextureBindGroup;
    }

    public static UiPipeline Create(World world, Entity device, Entity queue, WGPUTextureFormat targetFormat)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();

        var wgsl = UiShaderSource.Load();
        var shaderModule = world.OwnWgpu(Wgpu.CreateWgslShaderModule(deviceHandle, wgsl, "ui_node"));

        var viewBindGroupLayout = world.OwnWgpu(CreateViewBindGroupLayout(deviceHandle));
        var textureBindGroupLayout = world.OwnWgpu(CreateTextureBindGroupLayout(deviceHandle));
        var pipelineLayout = world.OwnWgpu(
            CreatePipelineLayout(deviceHandle, viewBindGroupLayout, textureBindGroupLayout));

        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            targetFormat));

        var viewUniformBuffer = world.CreateWgpuBuffer(device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
            Size = 64, // one mat4x4<f32>
            MappedAtCreation = 0
        });

        var (defaultTexture, defaultTextureView) = CreateDefaultWhiteTexture(world, device, queue);
        var defaultSampler = world.CreateWgpuSampler(device, DefaultSamplerDescriptor());

        var viewBindGroup = world.OwnWgpu(CreateViewBindGroup(
            deviceHandle, viewBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(), viewUniformBuffer.GetWgpu<WGPUBuffer>()));
        var defaultTextureBindGroup = world.OwnWgpu(CreateTextureBindGroup(
            deviceHandle, textureBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            defaultTextureView.GetWgpu<WGPUTextureView>(), defaultSampler.GetWgpu<WGPUSampler>()));

        return new UiPipeline(
            device, queue, shaderModule, viewBindGroupLayout, textureBindGroupLayout, pipelineLayout,
            renderPipeline, viewUniformBuffer, defaultTexture, defaultTextureView, defaultSampler,
            viewBindGroup, defaultTextureBindGroup);
    }

    private static WgpuHandle<WGPUBindGroupLayout> CreateViewBindGroupLayout(WgpuHandle<WGPUDevice> device)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[1];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = 0;
        entries[0].Visibility = WGPUShaderStage.Vertex;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = 1;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroupLayout(device, in descriptor);
        }
    }

    private static WgpuHandle<WGPUBindGroupLayout> CreateTextureBindGroupLayout(WgpuHandle<WGPUDevice> device)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[2];

        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = 0;
        entries[0].Visibility = WGPUShaderStage.Fragment;
        entries[0].Texture = WGPUTextureBindingLayout.Default;
        entries[0].Texture.SampleType = WGPUTextureSampleType.Float;
        entries[0].Texture.ViewDimension = WGPUTextureViewDimension._2D;

        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = 1;
        entries[1].Visibility = WGPUShaderStage.Fragment;
        entries[1].Sampler = WGPUSamplerBindingLayout.Default;
        entries[1].Sampler.Type = WGPUSamplerBindingType.Filtering;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = 2;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroupLayout(device, in descriptor);
        }
    }

    private static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device, Entity viewLayout, Entity textureLayout)
    {
        var viewLayoutPtr = (WGPUBindGroupLayout*)viewLayout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        var textureLayoutPtr = (WGPUBindGroupLayout*)textureLayout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();

        Span<nint> layouts = [(nint)viewLayoutPtr, (nint)textureLayoutPtr];
        fixed (nint* layoutsPtr = layouts) {
            var descriptor = WGPUPipelineLayoutDescriptor.Default;
            descriptor.BindGroupLayoutCount = 2;
            descriptor.BindGroupLayouts = (WGPUBindGroupLayout**)layoutsPtr;
            return Wgpu.CreatePipelineLayout(device, in descriptor);
        }
    }

    private static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat targetFormat)
    {
        var attributes = UiVertexLayout.Attributes;
        Span<WGPUVertexAttribute> vertexAttributes = stackalloc WGPUVertexAttribute[attributes.Length];
        for (var i = 0; i < attributes.Length; i++) {
            vertexAttributes[i] = WGPUVertexAttribute.Default;
            vertexAttributes[i].Format = attributes[i].Format;
            vertexAttributes[i].Offset = attributes[i].Offset;
            vertexAttributes[i].ShaderLocation = attributes[i].ShaderLocation;
        }

        fixed (WGPUVertexAttribute* attributesPtr = vertexAttributes)
        fixed (byte* vertexEntry = "vertex"u8)
        fixed (byte* fragmentEntry = "fragment"u8) {
            var vertexBufferLayout = WGPUVertexBufferLayout.Default;
            vertexBufferLayout.StepMode = WGPUVertexStepMode.Vertex;
            vertexBufferLayout.ArrayStride = UiVertexLayout.Stride;
            vertexBufferLayout.AttributeCount = (nuint)attributes.Length;
            vertexBufferLayout.Attributes = attributesPtr;

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

            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Label = default;
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.DangerousGetHandle();

            descriptor.Vertex = WGPUVertexState.Default;
            descriptor.Vertex.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
            descriptor.Vertex.EntryPoint = new WGPUStringView { Data = vertexEntry, Length = 6 };
            descriptor.Vertex.BufferCount = 1;
            descriptor.Vertex.Buffers = &vertexBufferLayout;

            descriptor.Primitive = WGPUPrimitiveState.Default;
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
            descriptor.Primitive.CullMode = WGPUCullMode.None;

            descriptor.Multisample = WGPUMultisampleState.Default;

            var fragmentState = WGPUFragmentState.Default;
            fragmentState.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
            fragmentState.EntryPoint = new WGPUStringView { Data = fragmentEntry, Length = 8 };
            fragmentState.TargetCount = 1;
            fragmentState.Targets = &colorTarget;
            descriptor.Fragment = &fragmentState;

            return Wgpu.CreateRenderPipeline(device, in descriptor);
        }
    }

    private static (Entity Texture, Entity View) CreateDefaultWhiteTexture(World world, Entity device, Entity queue)
    {
        var texture = world.CreateWgpuTexture(device, new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D { Width = 1, Height = 1, DepthOrArrayLayers = 1 },
            Format = WGPUTextureFormat.RGBA8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null
        });

        Span<byte> whitePixel = [255, 255, 255, 255];
        var layout = new WGPUTexelCopyBufferLayout {
            Offset = 0,
            BytesPerRow = 4,
            RowsPerImage = 1
        };
        var copyTexture = new WGPUTexelCopyTextureInfo {
            Texture = (WGPUTexture*)texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
            MipLevel = 0,
            Origin = default,
            Aspect = WGPUTextureAspect.All
        };
        var extent = new WGPUExtent3D { Width = 1, Height = 1, DepthOrArrayLayers = 1 };
        fixed (byte* pixelPtr = whitePixel) {
            WgpuUnsafe.wgpuQueueWriteTexture(
                (WGPUQueue*)queue.GetWgpu<WGPUQueue>().DangerousGetHandle(),
                &copyTexture, pixelPtr, 4, &layout, &extent);
        }

        var view = world.CreateWgpuTextureView(texture, WGPUTextureViewDescriptor.Default with {
            Format = WGPUTextureFormat.RGBA8Unorm,
            Dimension = WGPUTextureViewDimension._2D,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Aspect = WGPUTextureAspect.All
        });

        return (texture, view);
    }

    private static WGPUSamplerDescriptor DefaultSamplerDescriptor()
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

    private static WgpuHandle<WGPUBindGroup> CreateViewBindGroup(
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUBindGroupLayout> layout, WgpuHandle<WGPUBuffer> uniformBuffer)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[1];
        entries[0] = WGPUBindGroupEntry.Default;
        entries[0].Binding = 0;
        entries[0].Buffer = (WGPUBuffer*)uniformBuffer.DangerousGetHandle();
        entries[0].Offset = 0;
        entries[0].Size = 64;

        fixed (WGPUBindGroupEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
            descriptor.EntryCount = 1;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroup(device, in descriptor);
        }
    }

    private static WgpuHandle<WGPUBindGroup> CreateTextureBindGroup(
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUBindGroupLayout> layout,
        WgpuHandle<WGPUTextureView> textureView, WgpuHandle<WGPUSampler> sampler)
    {
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
            return Wgpu.CreateBindGroup(device, in descriptor);
        }
    }
}

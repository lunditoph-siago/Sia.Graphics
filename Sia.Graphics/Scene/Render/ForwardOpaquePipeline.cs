using Sia;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed unsafe class ForwardOpaquePipeline
{
    internal Entity RenderPipeline { get; }
    internal Entity BindGroupLayout { get; }
    internal Entity LightingBindGroupLayout { get; }
    internal Entity IblBindGroupLayout { get; }

    private ForwardOpaquePipeline(
        Entity renderPipeline, Entity bindGroupLayout, Entity lightingBindGroupLayout, Entity iblBindGroupLayout)
    {
        RenderPipeline = renderPipeline;
        BindGroupLayout = bindGroupLayout;
        LightingBindGroupLayout = lightingBindGroupLayout;
        IblBindGroupLayout = iblBindGroupLayout;
    }

    public static ForwardOpaquePipeline Create(
        World world, Entity device, WGPUTextureFormat colorFormat, WGPUTextureFormat depthFormat)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var shaderModule = world.OwnWgpu(
            Wgpu.CreateWgslShaderModule(deviceHandle, SceneShaderSource.LoadForwardPbr(), "forward_pbr"));
        var bindGroupLayout = world.OwnWgpu(SceneBindGroupLayout.Create(
            deviceHandle, instanceVisibility: WGPUShaderStage.Vertex | WGPUShaderStage.Fragment));
        var lightingBindGroupLayout = world.OwnWgpu(SceneLightingBindGroupLayout.Create(deviceHandle));
        var iblBindGroupLayout = world.OwnWgpu(SceneIblBindGroupLayout.Create(deviceHandle));
        var pipelineLayout = world.OwnWgpu(SceneBindGroupLayout.CreatePipelineLayout(
            deviceHandle,
            bindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            lightingBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            iblBindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            colorFormat,
            depthFormat));
        return new ForwardOpaquePipeline(renderPipeline, bindGroupLayout, lightingBindGroupLayout, iblBindGroupLayout);
    }

    private static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat)
    {
        var vertexEntryPoint = "vertex"u8;
        var fragmentEntryPoint = "fragment"u8;
        fixed (byte* vertexEntry = vertexEntryPoint)
        fixed (byte* fragmentEntry = fragmentEntryPoint) {
            Span<WGPUVertexAttribute> attributes = stackalloc WGPUVertexAttribute[SceneVertexLayout.AttributeCount];
            SceneVertexLayout.Fill(attributes);

            fixed (WGPUVertexAttribute* attributesPtr = attributes) {
                var vertexBuffer = WGPUVertexBufferLayout.Default;
                vertexBuffer.StepMode = WGPUVertexStepMode.Vertex;
                vertexBuffer.ArrayStride = MeshVertex.Stride;
                vertexBuffer.AttributeCount = (nuint)attributes.Length;
                vertexBuffer.Attributes = attributesPtr;

                var colorTarget = WGPUColorTargetState.Default;
                colorTarget.Format = colorFormat;
                colorTarget.WriteMask = WGPUColorWriteMask.All;

                var fragment = WGPUFragmentState.Default;
                fragment.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
                fragment.EntryPoint = new WGPUStringView {
                    Data = fragmentEntry,
                    Length = (nuint)fragmentEntryPoint.Length
                };
                fragment.TargetCount = 1;
                fragment.Targets = &colorTarget;

                var depthStencil = WGPUDepthStencilState.Default;
                depthStencil.Format = depthFormat;
                depthStencil.DepthWriteEnabled = WGPUOptionalBool.False;
                depthStencil.DepthCompare = WGPUCompareFunction.LessEqual;

                var descriptor = WGPURenderPipelineDescriptor.Default;
                descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.DangerousGetHandle();
                descriptor.Vertex = WGPUVertexState.Default;
                descriptor.Vertex.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
                descriptor.Vertex.EntryPoint = new WGPUStringView {
                    Data = vertexEntry,
                    Length = (nuint)vertexEntryPoint.Length
                };
                descriptor.Vertex.BufferCount = 1;
                descriptor.Vertex.Buffers = &vertexBuffer;
                descriptor.Primitive = WGPUPrimitiveState.Default;
                descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
                descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
                descriptor.Primitive.CullMode = WGPUCullMode.Back;
                descriptor.DepthStencil = &depthStencil;
                descriptor.Multisample = WGPUMultisampleState.Default;
                descriptor.Fragment = &fragment;
                return Wgpu.CreateRenderPipeline(device, in descriptor);
            }
        }
    }
}

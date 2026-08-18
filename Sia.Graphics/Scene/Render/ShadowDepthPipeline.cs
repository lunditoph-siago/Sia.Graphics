using Sia;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed unsafe class ShadowDepthPipeline
{
    internal Entity RenderPipeline { get; }
    internal Entity BindGroupLayout { get; }

    private ShadowDepthPipeline(Entity renderPipeline, Entity bindGroupLayout)
    {
        RenderPipeline = renderPipeline;
        BindGroupLayout = bindGroupLayout;
    }

    public static ShadowDepthPipeline Create(World world, Entity device)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var shaderModule = world.OwnWgpu(
            Wgpu.CreateWgslShaderModule(deviceHandle, SceneShaderSource.LoadShadowDepth(), "shadow_depth"));
        var bindGroupLayout = world.OwnWgpu(SceneBindGroupLayout.Create(
            deviceHandle, instanceVisibility: WGPUShaderStage.Vertex));
        var pipelineLayout = world.OwnWgpu(SceneBindGroupLayout.CreatePipelineLayout(
            deviceHandle, bindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>()));
        return new ShadowDepthPipeline(renderPipeline, bindGroupLayout);
    }

    private static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout)
    {
        var vertexEntryPoint = "vertex"u8;
        fixed (byte* vertexEntry = vertexEntryPoint) {
            Span<WGPUVertexAttribute> attributes = stackalloc WGPUVertexAttribute[SceneVertexLayout.AttributeCount];
            SceneVertexLayout.Fill(attributes);

            fixed (WGPUVertexAttribute* attributesPtr = attributes) {
                var vertexBuffer = WGPUVertexBufferLayout.Default;
                vertexBuffer.StepMode = WGPUVertexStepMode.Vertex;
                vertexBuffer.ArrayStride = MeshVertex.Stride;
                vertexBuffer.AttributeCount = (nuint)attributes.Length;
                vertexBuffer.Attributes = attributesPtr;

                var depthStencil = WGPUDepthStencilState.Default;
                depthStencil.Format = WGPUTextureFormat.Depth32Float;
                depthStencil.DepthWriteEnabled = WGPUOptionalBool.True;
                depthStencil.DepthCompare = WGPUCompareFunction.Less;
                depthStencil.DepthBias = 2;
                depthStencil.DepthBiasSlopeScale = 2.0f;

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
                descriptor.Fragment = null;
                return Wgpu.CreateRenderPipeline(device, in descriptor);
            }
        }
    }
}

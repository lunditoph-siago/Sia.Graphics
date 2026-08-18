using Sia;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed class SceneRenderer(DepthPrepassPipeline depthPipeline, ForwardOpaquePipeline forwardPipeline)
{
    private readonly InstanceGpuStore _instances = new();
    private readonly CameraUniforms _cameraUniforms = new();
    private Entity _depthBindGroup;
    private Entity _forwardBindGroup;
    private IReadOnlyList<int> _visible = [];

    public void PrepareFrame(in GpuFrame frame, Entity cameraEntity)
    {
        var cache = frame.World.AcquireAddon<SceneRenderCache>();
        cache.Refresh();

        var matrices = cameraEntity.Get<CameraMatrices>();
        _cameraUniforms.Update(in frame, in matrices);

        var resized = _instances.Upload(in frame, cache.Data);
        if (resized || !_depthBindGroup.IsValid) {
            EnsureBindGroups(in frame);
        }

        _visible = cache.Cull(matrices.Frustum);
    }

    public void EncodeDepthPrepass(in GpuFrame frame, WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Encode(in frame, renderPass, depthPipeline.RenderPipeline, _depthBindGroup);

    public void EncodeForwardOpaque(in GpuFrame frame, WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Encode(in frame, renderPass, forwardPipeline.RenderPipeline, _forwardBindGroup);

    private void Encode(
        in GpuFrame frame,
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        Entity pipeline, Entity bindGroup)
    {
        if (_visible.Count == 0) {
            return;
        }

        var cache = frame.World.AcquireAddon<SceneRenderCache>();
        var meshStore = frame.World.AcquireAddon<MeshGpuStore>();
        var meshRegistry = frame.World.AcquireAddon<MeshRegistry>();

        Wgpu.SetRenderPipeline(renderPass, pipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, bindGroup.GetWgpu<WGPUBindGroup>());

        foreach (var index in _visible) {
            var handle = cache.MeshHandles[index];
            var mesh = meshStore.GetOrUpload(in frame, meshRegistry, handle);
            Wgpu.SetVertexBuffer(renderPass, 0, mesh.VertexBuffer.GetWgpu<WGPUBuffer>());
            Wgpu.SetIndexBuffer(renderPass, mesh.IndexBuffer.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
            Wgpu.DrawIndexed(renderPass, mesh.IndexCount, instanceCount: 1, firstInstance: (uint)index);
        }
    }

    private void EnsureBindGroups(in GpuFrame frame)
    {
        if (_depthBindGroup.IsValid) {
            _depthBindGroup.Destroy();
        }
        if (_forwardBindGroup.IsValid) {
            _forwardBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        var cameraBuffer = _cameraUniforms.Buffer.GetWgpu<WGPUBuffer>();
        var instanceBuffer = _instances.Buffer.GetWgpu<WGPUBuffer>();

        _depthBindGroup = frame.World.OwnWgpu(SceneBindGroupLayout.CreateBindGroup(
            deviceHandle,
            depthPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            cameraBuffer, instanceBuffer, _instances.Capacity));
        _forwardBindGroup = frame.World.OwnWgpu(SceneBindGroupLayout.CreateBindGroup(
            deviceHandle,
            forwardPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            cameraBuffer, instanceBuffer, _instances.Capacity));
    }
}

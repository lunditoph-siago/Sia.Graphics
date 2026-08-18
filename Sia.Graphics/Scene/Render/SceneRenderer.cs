using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed class SceneRenderer(
    DepthPrepassPipeline depthPipeline,
    ForwardOpaquePipeline forwardPipeline,
    ClusterLightCullingPipeline cullingPipeline,
    ShadowDepthPipeline shadowDepthPipeline)
{
    private readonly InstanceGpuStore _instances = new();
    private readonly CameraUniforms _cameraUniforms = new();
    private readonly LightGpuStore _lights = new();
    private readonly ClusterGridBuffers _clusterBuffers = new();
    private readonly ShadowAtlasGpuStore _shadowAtlas = new();
    private readonly ShadowGpuStore _shadows = new();
    private Entity _depthBindGroup;
    private Entity _forwardBindGroup;
    private Entity _forwardLightingBindGroup;
    private Entity _cullingBindGroup;
    private Entity _shadowCameraBuffer;
    private Entity _shadowDrawBindGroup;
    private IReadOnlyList<int> _visible = [];

    public LightGpuStore Lights => _lights;

    public ClusterGridBuffers ClusterBuffers => _clusterBuffers;

    public ShadowGpuStore Shadows => _shadows;

    public ShadowAtlasGpuStore ShadowAtlas => _shadowAtlas;

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

    public void PrepareLighting(
        in GpuFrame frame, ClusterGridConfig clusterConfig, ShadowAtlasConfig shadowConfig, Entity cameraEntity)
    {
        _shadows.Refresh(frame.World, shadowConfig, cameraEntity);
        var atlasResized = _shadowAtlas.EnsureCapacity(in frame, shadowConfig);
        var shadowLayersResized = _shadows.Upload(in frame, shadowConfig);
        EnsureShadowCameraBuffer(in frame);
        if (atlasResized || !_shadowDrawBindGroup.IsValid) {
            EnsureShadowDrawBindGroup(in frame);
        }

        _lights.Refresh(frame.World, _shadows);
        var lightsResized = _lights.Upload(in frame);
        var buffersResized = _clusterBuffers.EnsureCapacity(in frame, clusterConfig);

        var camera = cameraEntity.Get<Camera>();
        var matrices = cameraEntity.Get<CameraMatrices>();
        var viewport = frame.World.AcquireAddon<Viewport>().Value;
        _clusterBuffers.UpdateConfig(
            in frame, clusterConfig, in matrices, camera.Near, camera.Far,
            _lights.ClusteredLights.Count, (uint)viewport.Width, (uint)viewport.Height);
        _clusterBuffers.ResetCursor(in frame);

        if (lightsResized || buffersResized || !_cullingBindGroup.IsValid) {
            EnsureCullingBindGroup(in frame);
        }
        if (lightsResized || buffersResized || atlasResized || shadowLayersResized || !_forwardLightingBindGroup.IsValid) {
            EnsureForwardLightingBindGroup(in frame);
        }
    }

    public void EncodeClusterLightCulling(
        in GpuFrame frame, ClusterGridConfig clusterConfig, WgpuHandle<WGPUComputePassEncoder> computePass)
    {
        Wgpu.SetComputePipeline(computePass, cullingPipeline.ComputePipeline.GetWgpu<WGPUComputePipeline>());
        Wgpu.SetBindGroup(computePass, 0, _cullingBindGroup.GetWgpu<WGPUBindGroup>());
        var workgroups = (clusterConfig.ClusterCount + 63) / 64;
        Wgpu.DispatchWorkgroups(computePass, workgroups);
    }

    public void EncodeShadowLayer(in GpuFrame frame, int layer, WgpuHandle<WGPURenderPassEncoder> renderPass)
    {
        var cache = frame.World.AcquireAddon<SceneRenderCache>();
        if (cache.Data.Length == 0) {
            return;
        }

        Wgpu.WriteBuffer(
            frame.Queue.GetWgpu<WGPUQueue>(), _shadowCameraBuffer.GetWgpu<WGPUBuffer>(), 0,
            [new CameraUniformData(_shadows.LayerViewProj(layer), float4.zero)]);

        Wgpu.SetRenderPipeline(renderPass, shadowDepthPipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, _shadowDrawBindGroup.GetWgpu<WGPUBindGroup>());
        for (var index = 0; index < cache.Data.Length; index++) {
            var handle = cache.MeshHandles[index];
            var meshStore = frame.World.AcquireAddon<MeshGpuStore>();
            var meshRegistry = frame.World.AcquireAddon<MeshRegistry>();
            var mesh = meshStore.GetOrUpload(in frame, meshRegistry, handle);
            Wgpu.SetVertexBuffer(renderPass, 0, mesh.VertexBuffer.GetWgpu<WGPUBuffer>());
            Wgpu.SetIndexBuffer(renderPass, mesh.IndexBuffer.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
            Wgpu.DrawIndexed(renderPass, mesh.IndexCount, instanceCount: 1, firstInstance: (uint)index);
        }
    }

    private void EnsureShadowCameraBuffer(in GpuFrame frame)
    {
        if (_shadowCameraBuffer.IsValid) {
            return;
        }
        _shadowCameraBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
            Size = CameraUniformData.Stride,
            MappedAtCreation = 0
        });
    }

    private void EnsureShadowDrawBindGroup(in GpuFrame frame)
    {
        if (_shadowDrawBindGroup.IsValid) {
            _shadowDrawBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        _shadowDrawBindGroup = frame.World.OwnWgpu(SceneBindGroupLayout.CreateBindGroup(
            deviceHandle,
            shadowDepthPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            _shadowCameraBuffer.GetWgpu<WGPUBuffer>(),
            _instances.IsValid ? _instances.Buffer.GetWgpu<WGPUBuffer>() : default,
            _instances.Capacity));
    }

    private void EnsureCullingBindGroup(in GpuFrame frame)
    {
        if (_cullingBindGroup.IsValid) {
            _cullingBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        _cullingBindGroup = frame.World.OwnWgpu(ClusterCullingBindGroupLayout.CreateBindGroup(
            deviceHandle,
            cullingPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            _clusterBuffers.ConfigBuffer.GetWgpu<WGPUBuffer>(),
            _lights.ClusteredBuffer.GetWgpu<WGPUBuffer>(), _lights.ClusteredCapacity,
            _clusterBuffers.LightGridBuffer.GetWgpu<WGPUBuffer>(), _clusterBuffers.LightGridSize,
            _clusterBuffers.LightIndexListBuffer.GetWgpu<WGPUBuffer>(), _clusterBuffers.LightIndexListCapacity,
            _clusterBuffers.CursorBuffer.GetWgpu<WGPUBuffer>()));
    }

    private void EnsureForwardLightingBindGroup(in GpuFrame frame)
    {
        if (_forwardLightingBindGroup.IsValid) {
            _forwardLightingBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        _forwardLightingBindGroup = frame.World.OwnWgpu(SceneLightingBindGroupLayout.CreateBindGroup(
            deviceHandle,
            forwardPipeline.LightingBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            _clusterBuffers.ConfigBuffer.GetWgpu<WGPUBuffer>(),
            _lights.ClusteredBuffer.GetWgpu<WGPUBuffer>(), _lights.ClusteredCapacity,
            _clusterBuffers.LightGridBuffer.GetWgpu<WGPUBuffer>(), _clusterBuffers.LightGridSize,
            _clusterBuffers.LightIndexListBuffer.GetWgpu<WGPUBuffer>(), _clusterBuffers.LightIndexListCapacity,
            _lights.DirectionalBuffer.GetWgpu<WGPUBuffer>(),
            _shadowAtlas.SamplingView.GetWgpu<WGPUTextureView>(),
            _shadowAtlas.Sampler.GetWgpu<WGPUSampler>(),
            _shadows.LayerBuffer.GetWgpu<WGPUBuffer>(), _shadows.LayerBufferCapacity,
            _shadows.ConfigBuffer.GetWgpu<WGPUBuffer>()));
    }

    public void EncodeDepthPrepass(in GpuFrame frame, WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Encode(in frame, renderPass, depthPipeline.RenderPipeline, _depthBindGroup, default);

    public void EncodeForwardOpaque(in GpuFrame frame, WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Encode(in frame, renderPass, forwardPipeline.RenderPipeline, _forwardBindGroup, _forwardLightingBindGroup);

    private void Encode(
        in GpuFrame frame,
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        Entity pipeline, Entity bindGroup, Entity lightingBindGroup)
    {
        if (_visible.Count == 0) {
            return;
        }

        var cache = frame.World.AcquireAddon<SceneRenderCache>();
        var meshStore = frame.World.AcquireAddon<MeshGpuStore>();
        var meshRegistry = frame.World.AcquireAddon<MeshRegistry>();

        Wgpu.SetRenderPipeline(renderPass, pipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, bindGroup.GetWgpu<WGPUBindGroup>());
        if (lightingBindGroup.IsValid) {
            Wgpu.SetBindGroup(renderPass, 1, lightingBindGroup.GetWgpu<WGPUBindGroup>());
        }

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

        if (_shadowDrawBindGroup.IsValid) {
            EnsureShadowDrawBindGroup(in frame);
        }
    }
}

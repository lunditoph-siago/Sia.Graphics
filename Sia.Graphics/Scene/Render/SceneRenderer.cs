using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

public sealed class SceneRenderer(
    DepthPrepassPipeline depthPipeline,
    ForwardOpaquePipeline forwardPipeline,
    ClusterLightCullingPipeline cullingPipeline,
    ShadowDepthPipeline shadowDepthPipeline,
    IblPrecomputePipelines iblPipelines)
{
    private static readonly IEntityMatcher _directionalMatcher =
        Matchers.Of<DirectionalLight, LightColor, GlobalTransform>();

    private readonly InstanceGpuStore _instances = new();
    private readonly CameraUniforms _cameraUniforms = new();
    private readonly LightGpuStore _lights = new();
    private readonly ClusterGridBuffers _clusterBuffers = new();
    private readonly ShadowAtlasGpuStore _shadowAtlas = new();
    private readonly ShadowGpuStore _shadows = new();
    private readonly IblEnvironmentGpuStore _ibl = new();
    private Entity _depthBindGroup;
    private Entity _forwardBindGroup;
    private Entity _forwardLightingBindGroup;
    private Entity _cullingBindGroup;
    private Entity[] _shadowCameraBuffers = [];
    private Entity[] _shadowDrawBindGroups = [];
    private Entity _iblPrefilterParamsBuffer;
    private Entity _iblPrefilterBindGroup;
    private Entity _iblBindGroup;
    private bool _iblBaked;
    private IReadOnlyList<int> _visible = [];

    public LightGpuStore Lights => _lights;

    public ClusterGridBuffers ClusterBuffers => _clusterBuffers;

    public ShadowGpuStore Shadows => _shadows;

    public ShadowAtlasGpuStore ShadowAtlas => _shadowAtlas;

    public IblEnvironmentGpuStore Ibl => _ibl;

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
        var layerCount = shadowConfig.LayerCount;
        EnsureShadowCameraBuffers(in frame, layerCount);
        if (atlasResized || _shadowDrawBindGroups.Length != layerCount) {
            EnsureShadowDrawBindGroups(in frame, layerCount);
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

        PrepareIbl(in frame);
    }

    public void PrepareIbl(in GpuFrame frame)
    {
        var created = _ibl.EnsureCapacity(in frame);
        if (created) {
            EnsureIblPrefilterBindGroup(in frame);
            EnsureIblBindGroup(in frame);
        }
        if (!_iblBaked && _ibl.IsValid) {
            BakeIblSh(in frame);
            _iblBaked = true;
        }
    }

    private void BakeIblSh(in GpuFrame frame)
    {
        var sunDirection = math.normalize(new float3(0.4f, 1.0f, 0.3f));
        var sunColor = new float3(1.0f, 0.96f, 0.9f);

        frame.World.Query(_directionalMatcher, entity => {
            var lightColor = entity.Get<LightColor>();
            sunDirection = -math.normalize(entity.Get<GlobalTransform>().WorldMatrix.c2.xyz);
            sunColor = lightColor.Color * lightColor.Intensity;
        });

        var coefficients = IrradianceSh.Project(direction => SkyColor(direction, sunDirection, sunColor));
        _ibl.UploadSh(in frame, coefficients);
    }

    private const float _skyExposure = 0.25f;

    private static float3 SkyColor(float3 dir, float3 sunDirection, float3 sunColor)
    {
        var horizon = new float3(0.55f, 0.6f, 0.68f);
        var zenith = new float3(0.12f, 0.24f, 0.55f);
        var ground = new float3(0.08f, 0.08f, 0.07f);
        var up = System.Math.Clamp(dir.y, -1.0f, 1.0f);
        var sky = math.lerp(horizon, zenith, System.Math.Clamp(up, 0.0f, 1.0f));
        var baseColor = math.lerp(ground, sky, SmoothStep(-0.15f, 0.05f, up));
        var sunAmount = MathF.Max(math.dot(dir, sunDirection), 0.0f);
        var sunGlow = sunColor * MathF.Pow(sunAmount, 256.0f) * 8.0f;
        return (baseColor + sunGlow) * _skyExposure;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = System.Math.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    public void EncodeIblPrefilter(
        in GpuFrame frame, int face, int mip, int mipCount, WgpuHandle<WGPURenderPassEncoder> renderPass)
    {
        var roughness = mipCount > 1 ? (float)mip / (mipCount - 1) : 0.0f;
        var sampleCount = mip == 0 ? 1 : 48;
        var sunDirection = math.normalize(new float3(0.4f, 1.0f, 0.3f));
        var sunColor = new float3(1.0f, 0.96f, 0.9f);
        frame.World.Query(_directionalMatcher, entity => {
            var lightColor = entity.Get<LightColor>();
            sunDirection = -math.normalize(entity.Get<GlobalTransform>().WorldMatrix.c2.xyz);
            sunColor = lightColor.Color * lightColor.Intensity;
        });

        Wgpu.WriteBuffer(
            frame.Queue.GetWgpu<WGPUQueue>(), _iblPrefilterParamsBuffer.GetWgpu<WGPUBuffer>(), 0,
            [new IblPrefilterParamsGpu(
                new float4(roughness, sampleCount, face, 0.0f),
                new float4(sunDirection, 0.0f),
                new float4(sunColor, 0.0f))]);

        Wgpu.SetRenderPipeline(renderPass, iblPipelines.PrefilterPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, _iblPrefilterBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(renderPass, vertexCount: 3);
    }

    public void EncodeIblBrdfLut(WgpuHandle<WGPURenderPassEncoder> renderPass)
    {
        Wgpu.SetRenderPipeline(renderPass, iblPipelines.BrdfLutPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.Draw(renderPass, vertexCount: 3);
    }

    private void EnsureIblPrefilterBindGroup(in GpuFrame frame)
    {
        if (!_iblPrefilterParamsBuffer.IsValid) {
            _iblPrefilterParamsBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = IblPrefilterParamsGpu.Stride,
                MappedAtCreation = 0
            });
        }
        if (_iblPrefilterBindGroup.IsValid) {
            _iblPrefilterBindGroup.Destroy();
        }
        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        _iblPrefilterBindGroup = frame.World.OwnWgpu(IblPrefilterBindGroupLayout.CreateBindGroup(
            deviceHandle,
            iblPipelines.PrefilterBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            _iblPrefilterParamsBuffer.GetWgpu<WGPUBuffer>()));
    }

    private void EnsureIblBindGroup(in GpuFrame frame)
    {
        if (_iblBindGroup.IsValid) {
            _iblBindGroup.Destroy();
        }
        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        _iblBindGroup = frame.World.OwnWgpu(SceneIblBindGroupLayout.CreateBindGroup(
            deviceHandle,
            forwardPipeline.IblBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            _ibl.ShBuffer.GetWgpu<WGPUBuffer>(),
            _ibl.PrefilteredSamplingView.GetWgpu<WGPUTextureView>(),
            _ibl.PrefilteredSampler.GetWgpu<WGPUSampler>(),
            _ibl.BrdfLutView.GetWgpu<WGPUTextureView>(),
            _ibl.BrdfLutSampler.GetWgpu<WGPUSampler>()));
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
            frame.Queue.GetWgpu<WGPUQueue>(), _shadowCameraBuffers[layer].GetWgpu<WGPUBuffer>(), 0,
            [new CameraUniformData(_shadows.LayerViewProj(layer), float4.zero)]);

        Wgpu.SetRenderPipeline(renderPass, shadowDepthPipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, _shadowDrawBindGroups[layer].GetWgpu<WGPUBindGroup>());
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

    private void EnsureShadowCameraBuffers(in GpuFrame frame, int layerCount)
    {
        if (_shadowCameraBuffers.Length == layerCount) {
            return;
        }
        var buffers = new Entity[layerCount];
        for (var layer = 0; layer < layerCount; layer++) {
            buffers[layer] = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = CameraUniformData.Stride,
                MappedAtCreation = 0
            });
        }
        _shadowCameraBuffers = buffers;
    }

    private void EnsureShadowDrawBindGroups(in GpuFrame frame, int layerCount)
    {
        foreach (var existing in _shadowDrawBindGroups) {
            if (existing.IsValid) {
                existing.Destroy();
            }
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        var bindGroups = new Entity[layerCount];
        for (var layer = 0; layer < layerCount; layer++) {
            bindGroups[layer] = frame.World.OwnWgpu(SceneBindGroupLayout.CreateBindGroup(
                deviceHandle,
                shadowDepthPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
                _shadowCameraBuffers[layer].GetWgpu<WGPUBuffer>(),
                _instances.IsValid ? _instances.Buffer.GetWgpu<WGPUBuffer>() : default,
                _instances.Capacity));
        }
        _shadowDrawBindGroups = bindGroups;
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
        Encode(in frame, renderPass, depthPipeline.RenderPipeline, _depthBindGroup, default, default);

    public void EncodeForwardOpaque(in GpuFrame frame, WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Encode(
            in frame, renderPass, forwardPipeline.RenderPipeline,
            _forwardBindGroup, _forwardLightingBindGroup, _iblBindGroup);

    private void Encode(
        in GpuFrame frame,
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        Entity pipeline, Entity bindGroup, Entity lightingBindGroup, Entity iblBindGroup)
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
        if (iblBindGroup.IsValid) {
            Wgpu.SetBindGroup(renderPass, 2, iblBindGroup.GetWgpu<WGPUBindGroup>());
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

        if (_shadowDrawBindGroups.Length > 0) {
            EnsureShadowDrawBindGroups(in frame, _shadowDrawBindGroups.Length);
        }
    }
}

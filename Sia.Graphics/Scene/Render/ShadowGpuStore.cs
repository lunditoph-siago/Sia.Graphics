using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ShadowViewProjGpu(float4x4 ViewProj)
{
    public const int Stride = 64;
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ShadowConfigGpu(float4 CascadeSplits, uint4 Params)
{
    public const int Stride = 32;
}

public sealed class ShadowGpuStore
{
    private static readonly IEntityMatcher _directionalMatcher =
        Matchers.Of<DirectionalLight, ShadowCaster, LightColor, GlobalTransform>();
    private static readonly IEntityMatcher _spotMatcher =
        Matchers.Of<SpotLight, ShadowCaster, LightColor, GlobalTransform>();

    private readonly Dictionary<Entity, int> _spotShadowLayerByEntity = [];
    private ShadowViewProjGpu[] _layerViewProj = [];
    private float[] _cascadeSplits = [];
    private Entity _layerBuffer;
    private ulong _layerBufferCapacity;
    private Entity _configBuffer;
    private bool _hasDirectionalShadow;
    private int _cascadeCount;

    public bool HasDirectionalShadow => _hasDirectionalShadow;

    public int CascadeCount => _cascadeCount;

    public IReadOnlyDictionary<Entity, int> SpotShadowLayerByEntity => _spotShadowLayerByEntity;

    public Entity LayerBuffer => _layerBuffer;

    public ulong LayerBufferCapacity => _layerBufferCapacity;

    public Entity ConfigBuffer => _configBuffer;

    public float4x4 LayerViewProj(int layer) =>
        layer >= 0 && layer < _layerViewProj.Length ? _layerViewProj[layer].ViewProj : float4x4.identity;

    public int ShadowLayerFor(Entity spotLightEntity) =>
        _spotShadowLayerByEntity.TryGetValue(spotLightEntity, out var layer) ? layer : -1;

    public void Refresh(World world, ShadowAtlasConfig config, Entity cameraEntity)
    {
        _cascadeCount = config.CascadeCount;
        _layerViewProj = new ShadowViewProjGpu[System.Math.Max(config.LayerCount, 1)];
        _spotShadowLayerByEntity.Clear();
        _hasDirectionalShadow = false;

        var camera = cameraEntity.Get<Camera>();
        var cameraTransform = cameraEntity.Get<GlobalTransform>().WorldMatrix;
        var viewport = world.AcquireAddon<Viewport>().Value;
        var aspect = viewport.Height > 0 ? viewport.Width / viewport.Height : 1.0f;
        var far = System.Math.Min(camera.Far, config.ShadowDistance);
        _cascadeSplits = CascadeSplitting.ComputeSplitDistances(
            camera.Near, far, config.CascadeCount, config.CascadeSplitLambda);

        world.Query(_directionalMatcher, entity => {
            if (_hasDirectionalShadow) {
                return;
            }
            _hasDirectionalShadow = true;
            var direction = -math.normalize(entity.Get<GlobalTransform>().WorldMatrix.c2.xyz);
            for (var i = 0; i < config.CascadeCount; i++) {
                var viewProj = CascadeSplitting.ComputeCascadeViewProj(
                    in cameraTransform, camera.VerticalFovRadians, aspect,
                    _cascadeSplits[i], _cascadeSplits[i + 1], direction, config.CascadeShadowPullback);
                _layerViewProj[i] = new ShadowViewProjGpu(viewProj);
            }
        });

        world.Query(_spotMatcher, entity => {
            if (_spotShadowLayerByEntity.Count >= config.MaxShadowedSpotLights) {
                return;
            }
            var light = entity.Get<SpotLight>();
            var worldMatrix = entity.Get<GlobalTransform>().WorldMatrix;
            var layer = config.CascadeCount + _spotShadowLayerByEntity.Count;
            var viewProj = CascadeSplitting.ComputeSpotViewProj(in worldMatrix, light.OuterAngle, light.Range);
            _layerViewProj[layer] = new ShadowViewProjGpu(viewProj);
            _spotShadowLayerByEntity[entity] = layer;
        });
    }

    public bool Upload(in GpuFrame frame, ShadowAtlasConfig config)
    {
        var resized = EnsureLayerBufferCapacity(in frame);
        if (_layerViewProj.Length > 0) {
            Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), _layerBuffer.GetWgpu<WGPUBuffer>(), 0, _layerViewProj);
        }

        if (!_configBuffer.IsValid) {
            _configBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = ShadowConfigGpu.Stride,
                MappedAtCreation = 0
            });
            resized = true;
        }

        var splits = new float4(
            _cascadeSplits.Length > 1 ? _cascadeSplits[1] : 1e9f,
            _cascadeSplits.Length > 2 ? _cascadeSplits[2] : 1e9f,
            _cascadeSplits.Length > 3 ? _cascadeSplits[3] : 1e9f,
            1e9f);
        var configData = new ShadowConfigGpu(
            splits,
            new uint4((uint)_cascadeCount, _hasDirectionalShadow ? 1u : 0u, (uint)config.CascadeCount, 0u));
        Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), _configBuffer.GetWgpu<WGPUBuffer>(), 0, [configData]);

        return resized;
    }

    private bool EnsureLayerBufferCapacity(in GpuFrame frame)
    {
        var requiredBytes = System.Math.Max((ulong)_layerViewProj.Length * ShadowViewProjGpu.Stride, ShadowViewProjGpu.Stride);
        if (_layerBufferCapacity >= requiredBytes) {
            return false;
        }

        if (_layerBuffer.IsValid) {
            _layerBuffer.Destroy();
        }
        _layerBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
            Size = requiredBytes,
            MappedAtCreation = 0
        });
        _layerBufferCapacity = requiredBytes;
        return true;
    }
}

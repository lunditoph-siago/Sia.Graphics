using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Graphics.Scene;

[StructLayout(LayoutKind.Sequential)]
public struct DirectionalLightBufferGpu
{
    public const int Stride = 144;

    public uint Count;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
    public DirectionalLightGpu Light0;
    public DirectionalLightGpu Light1;
    public DirectionalLightGpu Light2;
    public DirectionalLightGpu Light3;
}

public sealed class LightGpuStore
{
    public const int MaxDirectionalLights = 4;

    private static readonly IEntityMatcher _directionalMatcher =
        Matchers.Of<DirectionalLight, LightColor, GlobalTransform>();
    private static readonly IEntityMatcher _pointMatcher =
        Matchers.Of<PointLight, LightColor, GlobalTransform>();
    private static readonly IEntityMatcher _spotMatcher =
        Matchers.Of<SpotLight, LightColor, GlobalTransform>();

    private readonly List<ClusteredLightGpu> _clustered = [];
    private readonly DirectionalLightGpu[] _directional = new DirectionalLightGpu[MaxDirectionalLights];
    private int _directionalCount;

    private Entity _clusteredBuffer;
    private ulong _clusteredCapacity;
    private Entity _directionalBuffer;

    public IReadOnlyList<ClusteredLightGpu> ClusteredLights => _clustered;

    public Entity ClusteredBuffer => _clusteredBuffer;

    public ulong ClusteredCapacity => _clusteredCapacity;

    public Entity DirectionalBuffer => _directionalBuffer;

    public void Refresh(World world, ShadowGpuStore? shadows = null)
    {
        _clustered.Clear();
        _directionalCount = 0;

        world.Query(_directionalMatcher, entity => {
            if (_directionalCount >= MaxDirectionalLights) {
                return;
            }
            var forward = -math.normalize(entity.Get<GlobalTransform>().WorldMatrix.c2.xyz);
            var color = entity.Get<LightColor>();
            _directional[_directionalCount++] = DirectionalLightGpu.Create(forward, color.Color, color.Intensity);
        });

        world.Query(_pointMatcher, entity => {
            var position = entity.Get<GlobalTransform>().WorldMatrix.c3.xyz;
            var light = entity.Get<PointLight>();
            var color = entity.Get<LightColor>();
            _clustered.Add(ClusteredLightGpu.Point(position, light.Range, color.Color, color.Intensity));
        });

        world.Query(_spotMatcher, entity => {
            var worldMatrix = entity.Get<GlobalTransform>().WorldMatrix;
            var position = worldMatrix.c3.xyz;
            var direction = -math.normalize(worldMatrix.c2.xyz);
            var light = entity.Get<SpotLight>();
            var color = entity.Get<LightColor>();
            var shadowLayer = shadows?.ShadowLayerFor(entity) ?? -1;
            _clustered.Add(ClusteredLightGpu.Spot(
                position, direction, light.Range, color.Color, color.Intensity,
                MathF.Cos(light.InnerAngle), MathF.Cos(light.OuterAngle), shadowLayer));
        });
    }

    public bool Upload(in GpuFrame frame)
    {
        var resized = EnsureClusteredCapacity(frame);
        if (_clustered.Count > 0) {
            Wgpu.WriteBuffer(
                frame.Queue.GetWgpu<WGPUQueue>(), _clusteredBuffer.GetWgpu<WGPUBuffer>(), 0,
                CollectionsMarshal.AsSpan(_clustered));
        }

        if (!_directionalBuffer.IsValid) {
            _directionalBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = DirectionalLightBufferGpu.Stride,
                MappedAtCreation = 0
            });
        }

        var directionalData = new DirectionalLightBufferGpu {
            Count = (uint)_directionalCount,
            Light0 = _directional[0],
            Light1 = _directional[1],
            Light2 = _directional[2],
            Light3 = _directional[3],
        };
        Wgpu.WriteBuffer(
            frame.Queue.GetWgpu<WGPUQueue>(), _directionalBuffer.GetWgpu<WGPUBuffer>(), 0, [directionalData]);

        return resized;
    }

    private bool EnsureClusteredCapacity(in GpuFrame frame)
    {
        var requiredBytes = System.Math.Max((ulong)_clustered.Count * ClusteredLightGpu.Stride, ClusteredLightGpu.Stride);
        if (_clusteredCapacity >= requiredBytes) {
            return false;
        }

        var newCapacity = System.Math.Max(_clusteredCapacity, (ulong)ClusteredLightGpu.Stride * 64);
        while (newCapacity < requiredBytes) {
            newCapacity *= 2;
        }

        if (_clusteredBuffer.IsValid) {
            _clusteredBuffer.Destroy();
        }
        _clusteredBuffer = frame.World.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
            Size = newCapacity,
            MappedAtCreation = 0
        });
        _clusteredCapacity = newCapacity;
        return true;
    }
}

using Sia.RenderGraph;

namespace Sia.WebGPU;

public static class WgpuRenderGraphLowerer
{
    public static WgpuRenderGraphPlan Lower(CompiledRenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var buffers = graph.Buffers.Select(resource => {
            ValidateBufferUsage(resource);
            return new WgpuRenderGraphBufferPlan(
                resource,
                Lower(resource.Usage));
        }).ToArray();
        var textures = graph.Textures.Select(resource => {
            ValidateTextureUsage(resource);
            return new WgpuRenderGraphTexturePlan(
                resource,
                Lower(resource.Descriptor.Dimension),
                Lower(resource.Descriptor.Format),
                Lower(resource.Usage));
        }).ToArray();

        return new WgpuRenderGraphPlan(graph, buffers, textures);
    }

    internal static WGPUBufferUsage Lower(RenderGraphBufferUsage usage)
    {
        var result = WGPUBufferUsage.None;
        Add(usage, RenderGraphBufferUsage.MapRead, WGPUBufferUsage.MapRead, ref result);
        Add(usage, RenderGraphBufferUsage.MapWrite, WGPUBufferUsage.MapWrite, ref result);
        Add(usage, RenderGraphBufferUsage.CopySource, WGPUBufferUsage.CopySrc, ref result);
        Add(usage, RenderGraphBufferUsage.CopyDestination, WGPUBufferUsage.CopyDst, ref result);
        Add(usage, RenderGraphBufferUsage.Index, WGPUBufferUsage.Index, ref result);
        Add(usage, RenderGraphBufferUsage.Vertex, WGPUBufferUsage.Vertex, ref result);
        Add(usage, RenderGraphBufferUsage.Uniform, WGPUBufferUsage.Uniform, ref result);
        Add(usage, RenderGraphBufferUsage.Storage, WGPUBufferUsage.Storage, ref result);
        Add(usage, RenderGraphBufferUsage.Indirect, WGPUBufferUsage.Indirect, ref result);
        Add(usage, RenderGraphBufferUsage.QueryResolve, WGPUBufferUsage.QueryResolve, ref result);
        return result;
    }

    internal static WGPUTextureUsage Lower(RenderGraphTextureUsage usage)
    {
        var result = WGPUTextureUsage.None;
        Add(usage, RenderGraphTextureUsage.CopySource, WGPUTextureUsage.CopySrc, ref result);
        Add(usage, RenderGraphTextureUsage.CopyDestination, WGPUTextureUsage.CopyDst, ref result);
        Add(usage, RenderGraphTextureUsage.TextureBinding, WGPUTextureUsage.TextureBinding, ref result);
        Add(usage, RenderGraphTextureUsage.StorageBinding, WGPUTextureUsage.StorageBinding, ref result);
        Add(usage, RenderGraphTextureUsage.RenderAttachment, WGPUTextureUsage.RenderAttachment, ref result);
        Add(usage, RenderGraphTextureUsage.TransientAttachment, WGPUTextureUsage.TransientAttachment, ref result);
        return result;
    }

    internal static WGPUTextureDimension Lower(
        RenderGraphTextureDimension dimension) =>
        dimension switch {
            RenderGraphTextureDimension.D1 => WGPUTextureDimension._1D,
            RenderGraphTextureDimension.D2 => WGPUTextureDimension._2D,
            RenderGraphTextureDimension.D3 => WGPUTextureDimension._3D,
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };

    internal static WGPUTextureFormat Lower(RenderGraphTextureFormat format)
    {
        if (format == RenderGraphTextureFormat.Undefined ||
            !Enum.IsDefined(format)) {
            throw new NotSupportedException(
                $"Texture format {format} cannot be lowered to WebGPU.");
        }

        return (WGPUTextureFormat)(int)format;
    }

    private static void ValidateBufferUsage(CompiledRenderGraphBuffer resource)
    {
        var usage = resource.Usage;
        if (resource.Lifetime.IsUsed &&
            !resource.IsImported &&
            usage == RenderGraphBufferUsage.None) {
            throw new NotSupportedException(
                $"Buffer '{resource.Descriptor.Name}' has no WebGPU usage.");
        }
        if ((usage & RenderGraphBufferUsage.MapRead) != 0 &&
            (usage & ~(RenderGraphBufferUsage.MapRead |
                RenderGraphBufferUsage.CopyDestination)) != 0) {
            throw new NotSupportedException(
                $"Buffer '{resource.Descriptor.Name}' combines MapRead with unsupported WebGPU usage flags.");
        }
        if ((usage & RenderGraphBufferUsage.MapWrite) != 0 &&
            (usage & ~(RenderGraphBufferUsage.MapWrite |
                RenderGraphBufferUsage.CopySource)) != 0) {
            throw new NotSupportedException(
                $"Buffer '{resource.Descriptor.Name}' combines MapWrite with unsupported WebGPU usage flags.");
        }
    }

    private static void ValidateTextureUsage(CompiledRenderGraphTexture resource)
    {
        var usage = resource.Usage;
        if (resource.Lifetime.IsUsed &&
            !resource.IsImported &&
            usage == RenderGraphTextureUsage.None) {
            throw new NotSupportedException(
                $"Texture '{resource.Descriptor.Name}' has no WebGPU usage.");
        }
        if ((usage & RenderGraphTextureUsage.TransientAttachment) == 0) {
            return;
        }

        var allowed = RenderGraphTextureUsage.TransientAttachment |
            RenderGraphTextureUsage.RenderAttachment;
        if ((usage & RenderGraphTextureUsage.RenderAttachment) == 0 ||
            (usage & ~allowed) != 0 ||
            resource.IsImported ||
            resource.IsExported) {
            throw new NotSupportedException(
                $"Texture '{resource.Descriptor.Name}' uses TransientAttachment outside its WebGPU constraints.");
        }
    }

    private static void Add(
        RenderGraphBufferUsage usage,
        RenderGraphBufferUsage flag,
        WGPUBufferUsage lowered,
        ref WGPUBufferUsage result)
    {
        if ((usage & flag) != 0) {
            result |= lowered;
        }
    }

    private static void Add(
        RenderGraphTextureUsage usage,
        RenderGraphTextureUsage flag,
        WGPUTextureUsage lowered,
        ref WGPUTextureUsage result)
    {
        if ((usage & flag) != 0) {
            result |= lowered;
        }
    }
}

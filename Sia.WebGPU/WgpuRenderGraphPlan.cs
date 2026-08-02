using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphPlan
{
    internal WgpuRenderGraphPlan(
        CompiledRenderGraph graph,
        WgpuRenderGraphBufferPlan[] buffers,
        WgpuRenderGraphTexturePlan[] textures)
    {
        Graph = graph;
        Buffers = Array.AsReadOnly(buffers);
        Textures = Array.AsReadOnly(textures);
    }

    public CompiledRenderGraph Graph { get; }

    public RenderGraphStructureHash StructureHash => Graph.StructureHash;

    public IReadOnlyList<WgpuRenderGraphBufferPlan> Buffers { get; }

    public IReadOnlyList<WgpuRenderGraphTexturePlan> Textures { get; }
}

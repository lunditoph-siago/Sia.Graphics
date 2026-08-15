namespace Sia.WebGPU;

/// <summary>
/// Shared, per-execution-group state for <see cref="WgpuRenderGraphPassContext.GetOrBeginRenderPass"/>.
/// One instance is created per <see cref="Sia.RenderGraph.RenderGraphPassGroup"/> and handed to
/// every pass context within that group: the first pass to call
/// <see cref="WgpuRenderGraphPassContext.GetOrBeginRenderPass"/> actually opens the physical
/// render pass and records it here; every later pass in the same group gets the same encoder
/// back instead of opening its own. <see cref="WgpuRenderGraphExecutor"/> ends the pass once,
/// after the whole group has run.
/// </summary>
internal sealed class WgpuRenderGraphGroupRenderPassState
{
    public WgpuHandle<WGPURenderPassEncoder> Encoder { get; private set; }

    public bool IsOpen => !Encoder.IsNull;

    public void SetEncoder(WgpuHandle<WGPURenderPassEncoder> encoder)
    {
        Encoder = encoder;
    }

    internal void Reset()
    {
        Encoder = default;
    }
}

using Sia.RenderGraph;

namespace Sia.WebGPU;

/// <summary>
/// Reusable working buffers for <see cref="WgpuRenderGraphExecutor.Execute"/>. A render graph
/// executes every frame, and each execution needs the same handful of resolution tables
/// (resource handle -> native handle, ownership sets, transient view list); allocating fresh
/// collections for those every frame is pure GC churn for data whose peak size stabilizes after
/// the first few frames. Owned by <see cref="Sia.Graphics.Reactive.WgpuRenderGraphRegistry"/>
/// and cleared, not reallocated, between executions.
/// </summary>
public sealed class WgpuRenderGraphExecutionScratch
{
    internal readonly Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> Buffers = [];
    internal readonly Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> Textures = [];
    internal readonly HashSet<RenderGraphBufferHandle> OwnedBuffers = [];
    internal readonly HashSet<RenderGraphTextureHandle> OwnedTextures = [];
    internal readonly List<WgpuHandle<WGPUTextureView>> TransientViews = [];

    private readonly List<WgpuRenderGraphPassContext> _passContexts = [];
    private int _passContextCursor;
    private readonly List<WgpuRenderGraphGroupRenderPassState> _groupStates = [];
    private int _groupStateCursor;

    public void Clear()
    {
        Buffers.Clear();
        Textures.Clear();
        OwnedBuffers.Clear();
        OwnedTextures.Clear();
        TransientViews.Clear();
        _passContextCursor = 0;
        _groupStateCursor = 0;
    }

    internal WgpuRenderGraphGroupRenderPassState RentGroupState()
    {
        if (_groupStateCursor < _groupStates.Count) {
            var state = _groupStates[_groupStateCursor++];
            state.Reset();
            return state;
        }

        var created = new WgpuRenderGraphGroupRenderPassState();
        _groupStates.Add(created);
        _groupStateCursor++;
        return created;
    }

    internal WgpuRenderGraphPassContext RentPassContext(
        WgpuRenderGraphPlan plan,
        CompiledRenderGraphPass pass,
        WgpuHandle<WGPUCommandEncoder> commandEncoder,
        WgpuRenderGraphViewCache viewCache,
        WgpuRenderGraphGroupRenderPassState groupRenderPass)
    {
        if (_passContextCursor < _passContexts.Count) {
            var context = _passContexts[_passContextCursor++];
            context.Reset(plan, pass, commandEncoder, Buffers, Textures, viewCache, TransientViews, groupRenderPass);
            return context;
        }

        var created = new WgpuRenderGraphPassContext(
            plan, pass, commandEncoder, Buffers, Textures, viewCache, TransientViews, groupRenderPass);
        _passContexts.Add(created);
        _passContextCursor++;
        return created;
    }
}

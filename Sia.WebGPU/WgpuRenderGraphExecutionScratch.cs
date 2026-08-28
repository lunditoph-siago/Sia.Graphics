using Sia.RenderGraph;

namespace Sia.WebGPU;

public sealed class WgpuRenderGraphExecutionScratch
{
    internal readonly Dictionary<RenderGraphBufferHandle, WgpuHandle<WGPUBuffer>> _buffers = [];
    internal readonly Dictionary<RenderGraphTextureHandle, WgpuHandle<WGPUTexture>> _textures = [];
    internal readonly HashSet<RenderGraphBufferHandle> _ownedBuffers = [];
    internal readonly HashSet<RenderGraphTextureHandle> _ownedTextures = [];
    internal readonly List<WgpuHandle<WGPUTextureView>> _transientViews = [];

    private readonly List<WgpuRenderGraphPassContext> _passContexts = [];
    private int _passContextCursor;
    private readonly List<WgpuRenderGraphGroupRenderPassState> _groupStates = [];
    private int _groupStateCursor;

    public void Clear()
    {
        _buffers.Clear();
        _textures.Clear();
        _ownedBuffers.Clear();
        _ownedTextures.Clear();
        _transientViews.Clear();
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
            context.Reset(plan, pass, commandEncoder, _buffers, _textures, viewCache, _transientViews, groupRenderPass);
            return context;
        }

        var created = new WgpuRenderGraphPassContext(
            plan, pass, commandEncoder, _buffers, _textures, viewCache, _transientViews, groupRenderPass);
        _passContexts.Add(created);
        _passContextCursor++;
        return created;
    }
}

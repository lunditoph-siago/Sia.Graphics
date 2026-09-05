using Sia.RenderGraph;

namespace Sia.WebGPU;

internal sealed class WgpuRenderGraphGroupRenderPassState
{
    private WgpuRenderGraphColorAttachment[] _colors = [];
    private int _colorCount;
    private WgpuRenderGraphDepthStencilAttachment? _depthStencil;
    private RenderGraphPassHandle _pass;

    public WgpuHandle<WGPURenderPassEncoder> Encoder { get; private set; }

    public bool IsOpen => !Encoder.IsNull;

    public int RenderPassCount { get; private set; }

    public bool CanReuse(RenderGraphPassHandle pass,
        ReadOnlySpan<WgpuRenderGraphColorAttachment> colors,
        WgpuRenderGraphDepthStencilAttachment? depthStencil)
    {
        if (!IsOpen || colors.Length != _colorCount || depthStencil.HasValue != _depthStencil.HasValue) {
            return false;
        }
        if (pass == _pass && colors.SequenceEqual(_colors.AsSpan(0, _colorCount)) && depthStencil == _depthStencil) {
            return true;
        }
        for (var index = 0; index < colors.Length; index++) {
            var previous = _colors[index];
            var next = colors[index];
            if (previous.Texture != next.Texture || previous.Subresources != next.Subresources ||
                previous.StoreOp != WGPUStoreOp.Store || next.StoreOp != previous.StoreOp ||
                next.LoadOp != WGPULoadOp.Load) {
                return false;
            }
        }
        if (depthStencil is { } nextDepth && _depthStencil is { } previousDepth) {
            if (previousDepth.Texture != nextDepth.Texture || previousDepth.Subresources != nextDepth.Subresources ||
                previousDepth.DepthReadOnly != nextDepth.DepthReadOnly || previousDepth.StencilReadOnly != nextDepth.StencilReadOnly ||
                previousDepth.DepthStoreOp != nextDepth.DepthStoreOp || previousDepth.StencilStoreOp != nextDepth.StencilStoreOp ||
                (!nextDepth.DepthReadOnly && (nextDepth.DepthLoadOp != WGPULoadOp.Load || previousDepth.DepthStoreOp != WGPUStoreOp.Store)) ||
                (!nextDepth.StencilReadOnly && (nextDepth.StencilLoadOp != WGPULoadOp.Load || previousDepth.StencilStoreOp != WGPUStoreOp.Store))) {
                return false;
            }
        }
        _pass = pass;
        return true;
    }

    public void SetEncoder(WgpuHandle<WGPURenderPassEncoder> encoder,
        RenderGraphPassHandle pass,
        ReadOnlySpan<WgpuRenderGraphColorAttachment> colors,
        WgpuRenderGraphDepthStencilAttachment? depthStencil)
    {
        Encoder = encoder;
        _pass = pass;
        if (_colors.Length < colors.Length) {
            Array.Resize(ref _colors, colors.Length);
        }
        colors.CopyTo(_colors);
        _colorCount = colors.Length;
        _depthStencil = depthStencil;
    }

    public void End()
    {
        if (!IsOpen) {
            return;
        }
        var encoder = Encoder;
        Encoder = default;
        try {
            Wgpu.EndRenderPass(encoder);
        }
        finally {
            Wgpu.Release(ref encoder);
            RenderPassCount++;
        }
    }

    internal void Reset()
    {
        Encoder = default;
        RenderPassCount = 0;
        _colorCount = 0;
        _depthStencil = null;
    }
}

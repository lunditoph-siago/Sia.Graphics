namespace Sia.WebGPU;

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

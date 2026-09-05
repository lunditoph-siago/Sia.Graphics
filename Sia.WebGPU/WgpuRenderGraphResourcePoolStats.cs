namespace Sia.WebGPU;

public readonly record struct WgpuRenderGraphResourcePoolStats(
    int AvailableBuffers,
    int AvailableTextures,
    ulong CreatedBuffers,
    ulong CreatedTextures,
    ulong ReusedBuffers,
    ulong ReusedTextures);

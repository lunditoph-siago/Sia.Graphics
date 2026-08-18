using Sia;

namespace Sia.Graphics.Scene;

public sealed class ClusterGridConfig : IAddon
{
    public uint TilesX { get; set; } = 16;
    public uint TilesY { get; set; } = 9;
    public uint ZSlices { get; set; } = 24;
    public uint MaxLightIndicesPerCluster { get; set; } = 64;

    public uint ClusterCount => TilesX * TilesY * ZSlices;
}

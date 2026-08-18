using Sia;

namespace Sia.Graphics.Scene;

public sealed class ShadowAtlasConfig : IAddon
{
    public uint TileResolution { get; set; } = 1024;
    public int CascadeCount { get; set; } = 3;
    public float CascadeSplitLambda { get; set; } = 0.5f;
    public float CascadeShadowPullback { get; set; } = 2.0f;
    public float ShadowDistance { get; set; } = 40.0f;
    public int MaxShadowedSpotLights { get; set; } = 4;

    public int LayerCount => CascadeCount + MaxShadowedSpotLights;
}

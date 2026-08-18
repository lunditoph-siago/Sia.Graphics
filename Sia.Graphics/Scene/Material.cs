using Sia.Math;

namespace Sia.Graphics.Scene;

public record struct Material(
    float3 BaseColor,
    float Metallic,
    float Roughness,
    float3 EmissiveColor,
    float EmissiveStrength,
    int AlbedoTextureSlot = -1,
    int NormalTextureSlot = -1,
    int MetallicRoughnessTextureSlot = -1,
    int EmissiveTextureSlot = -1)
{
    public static Material Default => new(
        BaseColor: new float3(0.8f, 0.8f, 0.8f),
        Metallic: 0.0f,
        Roughness: 0.5f,
        EmissiveColor: float3.zero,
        EmissiveStrength: 0.0f);
}

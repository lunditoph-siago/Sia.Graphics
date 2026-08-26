namespace Sia.Spirv;

public readonly ref struct Texture2DArray
{
    public float Load(int x, int y, int layer, uint component) =>
        throw new PlatformNotSupportedException(
            "Texture arrays can only be accessed from a compiled SPIR-V shader.");

    public float SampleLevel(
        Sampler sampler,
        float u,
        float v,
        float layer,
        uint component) =>
        throw new PlatformNotSupportedException(
            "Texture arrays can only be accessed from a compiled SPIR-V shader.");
}

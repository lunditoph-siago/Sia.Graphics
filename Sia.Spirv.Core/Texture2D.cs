namespace Sia.Spirv;

public readonly ref struct Texture2D
{
    [SpirvIntrinsic(IntrinsicKind.Texture2DLoad)]
    public float Load(int x, int y, uint component) =>
        throw new PlatformNotSupportedException(
            "Textures can only be accessed from a compiled SPIR-V shader.");

    [SpirvIntrinsic(IntrinsicKind.Texture2DLoad)]
    public float Load(int x, int y, int level, uint component) =>
        throw new PlatformNotSupportedException(
            "Textures can only be accessed from a compiled SPIR-V shader.");

    [SpirvIntrinsic(IntrinsicKind.Texture2DSampleLevel)]
    public float SampleLevel(
        Sampler sampler,
        float u,
        float v,
        float level,
        uint component) =>
        throw new PlatformNotSupportedException(
            "Textures can only be accessed from a compiled SPIR-V shader.");
}

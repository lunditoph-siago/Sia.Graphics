namespace Sia.Spirv;

public readonly ref struct Texture2D
{
    [SpirvIntrinsic(IntrinsicKind.Texture2DLoad)]
    public float Load(int x, int y, uint component) =>
        throw new PlatformNotSupportedException(
            "Textures can only be accessed from a compiled SPIR-V shader.");
}

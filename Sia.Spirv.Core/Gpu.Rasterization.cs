namespace Sia.Spirv;

public static partial class Gpu
{
    public static uint VertexIndex {
        [SpirvIntrinsic(IntrinsicKind.VertexIndex)]
        get => throw CreatePlatformException();
    }

    public static uint InstanceIndex {
        [SpirvIntrinsic(IntrinsicKind.InstanceIndex)]
        get => throw CreatePlatformException();
    }

    [SpirvIntrinsic(IntrinsicKind.GetInput)]
    public static float GetInput(uint location, uint component) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.GetFlatInput)]
    public static float GetFlatInput(uint location, uint component) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.GetFragmentPosition)]
    public static float GetFragmentPosition(uint component) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Discard)]
    public static void Discard() =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.SetPosition)]
    public static void SetPosition(float x, float y, float z, float w) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.SetOutput)]
    public static void SetOutput(
        uint location,
        float x,
        float y,
        float z,
        float w) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.SetFlatOutput)]
    public static void SetFlatOutput(
        uint location,
        float x,
        float y,
        float z,
        float w) =>
        throw CreatePlatformException();
}

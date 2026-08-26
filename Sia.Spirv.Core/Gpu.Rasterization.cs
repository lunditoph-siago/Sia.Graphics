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

    [SpirvIntrinsic(IntrinsicKind.AsFloat)]
    public static float AsFloat(uint value) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.UnpackHalf)]
    public static float UnpackHalf(uint value, uint component) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Min)]
    public static float Min(float x, float y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Max)]
    public static float Max(float x, float y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.InverseSqrt)]
    public static float InverseSqrt(float value) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Saturate)]
    public static float Saturate(float value) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.LessThan)]
    public static uint LessThan(float x, float y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.LessThanOrEqual)]
    public static uint LessThanOrEqual(float x, float y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.GreaterThan)]
    public static uint GreaterThan(float x, float y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.GreaterThanOrEqual)]
    public static uint GreaterThanOrEqual(float x, float y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Equal)]
    public static uint Equal(float x, float y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Equal)]
    public static uint Equal(uint x, uint y) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Select)]
    public static float Select(float whenFalse, float whenTrue, uint condition) =>
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

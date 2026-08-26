namespace Sia.Spirv;

public static partial class Gpu
{
    public static uint VertexIndex =>
        throw CreatePlatformException();

    public static uint InstanceIndex =>
        throw CreatePlatformException();

    public static float GetInput(uint location, uint component) =>
        throw CreatePlatformException();

    public static float GetFlatInput(uint location, uint component) =>
        throw CreatePlatformException();

    public static float GetFragmentPosition(uint component) =>
        throw CreatePlatformException();

    public static float AsFloat(uint value) =>
        throw CreatePlatformException();

    public static float UnpackHalf(uint value, uint component) =>
        throw CreatePlatformException();

    public static float Min(float x, float y) =>
        throw CreatePlatformException();

    public static float Max(float x, float y) =>
        throw CreatePlatformException();

    public static float InverseSqrt(float value) =>
        throw CreatePlatformException();

    public static float Saturate(float value) =>
        throw CreatePlatformException();

    public static uint LessThan(float x, float y) =>
        throw CreatePlatformException();

    public static uint LessThanOrEqual(float x, float y) =>
        throw CreatePlatformException();

    public static uint GreaterThan(float x, float y) =>
        throw CreatePlatformException();

    public static uint GreaterThanOrEqual(float x, float y) =>
        throw CreatePlatformException();

    public static uint Equal(float x, float y) =>
        throw CreatePlatformException();

    public static uint Equal(uint x, uint y) =>
        throw CreatePlatformException();

    public static float Select(float whenFalse, float whenTrue, uint condition) =>
        throw CreatePlatformException();

    public static void Discard() =>
        throw CreatePlatformException();

    public static void SetPosition(float x, float y, float z, float w) =>
        throw CreatePlatformException();

    public static void SetOutput(
        uint location,
        float x,
        float y,
        float z,
        float w) =>
        throw CreatePlatformException();

    public static void SetFlatOutput(
        uint location,
        float x,
        float y,
        float z,
        float w) =>
        throw CreatePlatformException();
}

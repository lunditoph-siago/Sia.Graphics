namespace Sia.Spirv;

public static partial class Gpu
{
    public static uint VertexIndex =>
        throw CreatePlatformException();

    public static uint InstanceIndex =>
        throw CreatePlatformException();

    public static float GetInput(uint location, uint component) =>
        throw CreatePlatformException();

    public static float GetFragmentPosition(uint component) =>
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
}

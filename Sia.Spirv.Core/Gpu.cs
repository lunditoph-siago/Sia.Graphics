namespace Sia.Spirv;

public static partial class Gpu
{
    public static UInt3 GlobalInvocationId =>
        throw CreatePlatformException();

    public static UInt3 LocalInvocationId =>
        throw CreatePlatformException();

    public static UInt3 WorkGroupId =>
        throw CreatePlatformException();

    public static void Barrier() =>
        throw CreatePlatformException();

    private static PlatformNotSupportedException CreatePlatformException() =>
        new("GPU intrinsics can only be used from a compiled SPIR-V kernel.");
}

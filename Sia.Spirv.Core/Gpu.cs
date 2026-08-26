namespace Sia.Spirv;

public static partial class Gpu
{
    public static UInt3 GlobalInvocationId {
        [SpirvIntrinsic(IntrinsicKind.GlobalInvocationId)]
        get => throw CreatePlatformException();
    }

    public static UInt3 LocalInvocationId {
        [SpirvIntrinsic(IntrinsicKind.LocalInvocationId)]
        get => throw CreatePlatformException();
    }

    public static UInt3 WorkGroupId {
        [SpirvIntrinsic(IntrinsicKind.WorkGroupId)]
        get => throw CreatePlatformException();
    }

    [SpirvIntrinsic(IntrinsicKind.Barrier)]
    public static void Barrier() =>
        throw CreatePlatformException();

    private static PlatformNotSupportedException CreatePlatformException() =>
        new("GPU intrinsics can only be used from a compiled SPIR-V kernel.");
}

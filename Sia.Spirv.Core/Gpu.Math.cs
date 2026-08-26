namespace Sia.Spirv;

public static partial class Gpu
{
    [SpirvIntrinsic(IntrinsicKind.Sqrt)]
    public static float Sqrt(float value) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Sin)]
    public static float Sin(float value) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Cos)]
    public static float Cos(float value) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Pow)]
    public static float Pow(float value, float exponent) =>
        throw CreatePlatformException();

    [SpirvIntrinsic(IntrinsicKind.Abs)]
    public static float Abs(float value) =>
        throw CreatePlatformException();
}

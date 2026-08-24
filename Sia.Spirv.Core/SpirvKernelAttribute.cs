namespace Sia.Spirv;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SpirvKernelAttribute : Attribute
{
    public SpirvKernelAttribute(uint x, uint y = 1, uint z = 1)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public uint X { get; }

    public uint Y { get; }

    public uint Z { get; }
}

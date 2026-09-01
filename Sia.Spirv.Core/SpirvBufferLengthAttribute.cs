namespace Sia.Spirv;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class SpirvBufferLengthAttribute : Attribute
{
    public SpirvBufferLengthAttribute(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Length = length;
    }

    public int Length { get; }
}

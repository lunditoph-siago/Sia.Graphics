namespace Sia.Spirv;

[AttributeUsage(AttributeTargets.Field)]
public sealed class InterpolateAttribute : Attribute
{
    public InterpolateAttribute(
        InterpolationMode mode,
        InterpolationSampling sampling = InterpolationSampling.Center)
    {
        Mode = mode;
        Sampling = sampling;
    }

    public InterpolationMode Mode { get; }

    public InterpolationSampling Sampling { get; }
}

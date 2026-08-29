namespace Sia.Spirv;

[AttributeUsage(AttributeTargets.Field)]
public sealed class LocationAttribute(uint location) : Attribute
{
    public uint Location { get; } = location;
}

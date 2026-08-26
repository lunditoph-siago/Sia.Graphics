namespace Sia.Spirv;

/// <summary>
/// Marks a method as a recognized GPU operation; it always throws on the
/// CPU and is never actually executed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SpirvIntrinsicAttribute(IntrinsicKind kind) : Attribute
{
    public IntrinsicKind Kind { get; } = kind;
}

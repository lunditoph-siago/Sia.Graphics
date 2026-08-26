namespace Sia.Spirv;

/// <summary>
/// Declares that calls to this method are recognized by the SPIR-V
/// compiler as the given GPU operation. The method itself is a marker:
/// it always throws on the CPU and is never actually executed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SpirvIntrinsicAttribute(IntrinsicKind kind) : Attribute
{
    public IntrinsicKind Kind { get; } = kind;
}

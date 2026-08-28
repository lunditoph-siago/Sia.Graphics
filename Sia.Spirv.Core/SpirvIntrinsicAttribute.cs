namespace Sia.Spirv;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SpirvIntrinsicAttribute(IntrinsicKind kind) : Attribute
{
    public IntrinsicKind Kind { get; } = kind;
}

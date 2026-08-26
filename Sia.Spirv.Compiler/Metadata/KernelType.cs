namespace Sia.Spirv.Compiler.Metadata;

public sealed record KernelType(
    string Name,
    KernelType? ElementType = null,
    bool IsByReference = false)
{
    public static KernelType Void { get; } = new("System.Void");
}

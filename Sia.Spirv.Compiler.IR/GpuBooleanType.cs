namespace Sia.Spirv.Compiler.IR;

public sealed record GpuBooleanType : GpuType
{
    public static GpuBooleanType Instance { get; } = new();

    private GpuBooleanType()
    {
    }
}

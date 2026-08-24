namespace Sia.Spirv.Compiler.IR;

public sealed record GpuVoidType : GpuType
{
    public static GpuVoidType Instance { get; } = new();

    private GpuVoidType()
    {
    }
}

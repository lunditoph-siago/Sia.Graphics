namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvKernelParameter(
    string Name,
    int Position,
    SpirvKernelParameterKind Kind,
    SpirvScalarType ScalarType,
    SpirvStructLayout? StructLayout = null)
{
    public bool IsResource => Kind is not (
        SpirvKernelParameterKind.PushConstant or
        SpirvKernelParameterKind.WorkgroupMemory);
}

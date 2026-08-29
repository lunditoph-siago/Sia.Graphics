namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvKernelParameter(
    string Name,
    int Position,
    SpirvKernelParameterKind Kind,
    SpirvScalarType ScalarType,
    SpirvStructLayout? StructLayout = null,
    SpirvStageIoLayout? StageIoLayout = null)
{
    public bool IsResource => Kind is not (
        SpirvKernelParameterKind.StageInput or
        SpirvKernelParameterKind.PushConstant or
        SpirvKernelParameterKind.WorkgroupMemory);
}

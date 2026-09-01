using Sia.Spirv.Compiler.Legalization;

namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvKernelParameter(
    string Name,
    int Position,
    SpirvKernelParameterKind Kind,
    SpirvScalarType ScalarType,
    PhysicalStructLayout? PhysicalLayout = null,
    SpirvStageIoLayout? StageIoLayout = null,
    int? BufferLength = null)
{
    public bool IsResource => Kind is not (
        SpirvKernelParameterKind.StageInput or
        SpirvKernelParameterKind.PushConstant or
        SpirvKernelParameterKind.WorkgroupMemory);
}

namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvKernelParameter(
    string Name,
    int Position,
    SpirvKernelParameterKind Kind,
    SpirvScalarType ScalarType)
{
    public bool IsResource => Kind != SpirvKernelParameterKind.PushConstant;
}

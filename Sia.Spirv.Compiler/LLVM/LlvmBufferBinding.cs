using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.LLVM;

internal sealed record LlvmBufferBinding(
    LlvmValueType Type,
    SpirvKernelParameterKind Kind,
    int? ElementCount,
    PhysicalStructLayout? PhysicalLayout)
{
    public bool IsUniform => Kind == SpirvKernelParameterKind.UniformBuffer;

    public int AddressSpace => IsUniform ? 12 : 11;
}

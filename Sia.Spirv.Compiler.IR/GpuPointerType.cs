namespace Sia.Spirv.Compiler.IR;

public sealed record GpuPointerType(
    GpuType ElementType,
    GpuAddressSpace AddressSpace) : GpuType;

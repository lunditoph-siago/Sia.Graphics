namespace Sia.Spirv.Compiler.IR;

public sealed record GpuModule(IReadOnlyList<GpuFunction> Functions);

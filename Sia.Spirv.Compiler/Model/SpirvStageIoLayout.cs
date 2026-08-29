namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvStageIoLayout(
    string Name,
    IReadOnlyList<SpirvStageIoField> Fields);

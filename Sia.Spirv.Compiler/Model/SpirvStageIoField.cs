namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvStageIoField(
    string Name,
    int MetadataToken,
    SpirvStageIoKind Kind,
    SpirvScalarType Type,
    uint? Location = null,
    bool Flat = false);

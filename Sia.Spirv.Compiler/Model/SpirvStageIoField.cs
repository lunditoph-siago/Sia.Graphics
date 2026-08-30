using Sia.Spirv;

namespace Sia.Spirv.Compiler.Model;

public sealed record SpirvStageIoField(
    string Name,
    int MetadataToken,
    SpirvStageIoKind Kind,
    SpirvScalarType Type,
    uint? Location = null,
    InterpolationMode? Interpolation = null,
    InterpolationSampling? Sampling = null)
{
    public bool Flat => Interpolation == InterpolationMode.Flat;
}

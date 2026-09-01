namespace Sia.Graphics.Compatibility;

public sealed record GpuLegalizationPlan(
    IReadOnlyList<GpuBufferLegalization> Buffers)
{
    public bool IsSupported => Buffers.All(static buffer => buffer.IsSupported);
}

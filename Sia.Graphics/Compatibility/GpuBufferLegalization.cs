namespace Sia.Graphics.Compatibility;

public sealed record GpuBufferLegalization(
    GpuBufferRequirement Requirement,
    GpuBufferBindingKind BindingKind,
    string StrategyId,
    string Reason)
{
    public bool IsSupported => BindingKind != GpuBufferBindingKind.Unsupported;

    public bool RequiresLayoutLegalization => BindingKind == GpuBufferBindingKind.Uniform;
}

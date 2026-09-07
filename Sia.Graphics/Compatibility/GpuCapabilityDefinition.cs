using Sia.Graphics.Wgsl;

namespace Sia.Graphics.Compatibility;

public sealed record GpuCapabilityDefinition(
    string Id,
    GpuCapabilityKind Kind,
    WgslValueKind ValueKind,
    string? ShaderSymbol,
    string? CMember,
    GpuLimitComparison Comparison);

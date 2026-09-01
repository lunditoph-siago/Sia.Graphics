using Sia.Graphics.Compatibility;

namespace Sia.Graphics.UI;

internal sealed record UiLegalizationPlan(
    UiVertexDataMode VertexDataMode,
    GpuLegalizationPlan BufferPlan,
    string StrategyId);

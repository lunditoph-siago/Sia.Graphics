using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

public sealed record SpirvLegalizationPlan(
    SpirvKernel Kernel,
    IReadOnlyList<SpirvResourceLegalization> Resources)
{
    public IReadOnlyList<string> StrategyIds => Resources
        .Select(static resource => resource.StrategyId)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}

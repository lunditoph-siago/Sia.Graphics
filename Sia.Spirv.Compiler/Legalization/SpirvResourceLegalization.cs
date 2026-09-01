using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

public sealed record SpirvResourceLegalization(
    int ParameterPosition,
    SpirvKernelParameterKind SourceKind,
    SpirvKernelParameterKind TargetKind,
    string StrategyId);

namespace Sia.Spirv.Compiler.Tests.Fixtures;

public static class IlSemanticCases
{
    // Initial storage is [7, 0]. Expectations are calculated from the C# kernels,
    // independently of the managed and native output writers.
    public static TheoryData<string, uint, uint> StorageResults => new() {
        { nameof(MemorySemanticsShaders.SwapLoop), 1u, 2u },
        { nameof(MemorySemanticsShaders.SwitchAndContinue), 7u, 11u },
        { nameof(MemorySemanticsShaders.CaptureBeforeWrite), 10u, 18u },
        { nameof(MemorySemanticsShaders.RepeatedHelperCalls), 10u, 66u }
    };
}

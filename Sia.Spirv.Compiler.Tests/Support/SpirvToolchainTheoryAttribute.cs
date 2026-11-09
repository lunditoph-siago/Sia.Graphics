namespace Sia.Spirv.Compiler.Tests.Support;

public sealed class SpirvToolchainTheoryAttribute : TheoryAttribute
{
    public SpirvToolchainTheoryAttribute()
    {
        if (SpirvTestToolchain.Directory is null) Skip = SpirvTestToolchain.UnavailableReason;
    }
}
